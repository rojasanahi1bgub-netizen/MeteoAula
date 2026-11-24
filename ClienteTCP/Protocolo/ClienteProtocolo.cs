using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClienteTCP.Protocolo
{
    // Modelos de datos recibidos desde el servidor remoto
    public sealed record EstacionRemota(string Codigo, string Nombre, string Ciudad);

    public sealed record LecturaRemota(
        DateTime FechaHora,
        double TemperaturaCelsius,
        int HumedadPorcentaje,
        string Estado);

    public sealed record LecturaActualRemota(string CodigoEstacion, LecturaRemota Lectura);

    public sealed record ResumenRemoto(
        string CodigoEstacion,
        int TotalLecturas,
        double Promedio,
        double Minimo,
        double Maximo);

    // Representa un error devuelto por el servidor dentro del protocolo METEO/1.0
    public sealed class ErrorServidorException : Exception
    {
        public int Codigo { get; }

        // Inicializa la excepcion con el codigo de estado y el detalle del error
        public ErrorServidorException(int codigo, string mensaje) : base(mensaje)
        {
            Codigo = codigo;
        }
    }

    // Envia comandos del protocolo METEO/1.0 y valida las respuestas del servidor
    public sealed class ClienteProtocolo
    {
        private const string VERSION_PROTOCOLO = "1.0";
        private const int CODIGO_ERROR_MINIMO = 400;
        private const int CODIGO_ERROR_MAXIMO = 599;
        private const int HUMEDAD_MINIMA = 0;
        private const int HUMEDAD_MAXIMA = 100;

        private static readonly CultureInfo Cultura = CultureInfo.InvariantCulture;
        private static readonly string[] CiudadesValidas = { "UIO", "GYE", "CUE", "LOJ", "MEC" };
        private static readonly string[] EstadosTiempoValidos =
        {
            "Soleado", "Nublado", "ParcialmenteNublado", "Lluvioso", "Ventoso"
        };

        private readonly Stream flujo;

        // Inicializa el cliente del protocolo sobre un flujo de red activo
        public ClienteProtocolo(Stream flujoTcp)
        {
            if (flujoTcp == null)
            {
                throw new ArgumentNullException(nameof(flujoTcp));
            }

            flujo = flujoTcp;
        }

        // Envia el comando HELLO con la version del protocolo y verifica la bienvenida 200 WELCOME
        public void Saludar(string version = VERSION_PROTOCOLO)
        {
            TramaCliente.Enviar(flujo, "HELLO", version);
            string[] respuesta = LeerRespuesta();
            Exigir(respuesta, 3, "200", "WELCOME");

            if (respuesta[2] != VERSION_PROTOCOLO)
            {
                throw new InvalidDataException("Version de bienvenida incompatible");
            }
        }

        // Solicita el catalogo completo de estaciones mediante el comando LIST
        public IReadOnlyList<EstacionRemota> Listar()
        {
            TramaCliente.Enviar(flujo, "LIST");

            return LeerMultiple("LIST", "STATION", ParsearEstacion);
        }

        // Consulta la lectura mas reciente de una estacion mediante el comando CURRENT
        public LecturaActualRemota Actual(string codigo)
        {
            TramaCliente.Enviar(flujo, "CURRENT", codigo);
            string[] respuesta = LeerRespuesta();
            Exigir(respuesta, 7, "200", "CURRENT");
            ValidarCodigo(respuesta[2]);

            if (!string.Equals(respuesta[2], codigo.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Codigo de estacion no coincide con la consulta");
            }

            string[] camposLectura =
            {
                "211", "READING", respuesta[3], respuesta[4], respuesta[5], respuesta[6]
            };

            return new LecturaActualRemota(respuesta[2], ParsearLectura(camposLectura));
        }

        // Solicita las N lecturas mas recientes de una estacion mediante el comando HISTORY
        public IReadOnlyList<LecturaRemota> Historial(string codigo, int cantidadLecturas)
        {
            TramaCliente.Enviar(flujo, "HISTORY", codigo, cantidadLecturas.ToString(Cultura));
            List<LecturaRemota> resultado = LeerMultiple(
                "HISTORY",
                "READING",
                ParsearLectura,
                cantidadLecturas);

            for (int i = 1; i < resultado.Count; i++)
            {
                if (resultado[i].FechaHora < resultado[i - 1].FechaHora)
                {
                    throw new InvalidDataException("El historial no esta ordenado de antiguo a reciente");
                }
            }

            return resultado;
        }

        // Solicita el resumen estadistico de temperatura de una estacion mediante el comando SUMMARY
        public ResumenRemoto Resumen(string codigo)
        {
            TramaCliente.Enviar(flujo, "SUMMARY", codigo);
            string[] respuesta = LeerRespuesta();
            Exigir(respuesta, 7, "200", "SUMMARY");
            ValidarCodigo(respuesta[2]);

            if (!string.Equals(respuesta[2], codigo.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Codigo de estacion no coincide con la consulta");
            }

            int total = ConvertirEntero(respuesta[3], "TotalLecturas");
            double promedio = ConvertirNumero(respuesta[4], "Promedio");
            double minimo = ConvertirNumero(respuesta[5], "Minimo");
            double maximo = ConvertirNumero(respuesta[6], "Maximo");

            if (total < 1 || minimo > maximo || promedio < minimo || promedio > maximo)
            {
                throw new InvalidDataException("Resumen de temperatura inconsistente");
            }

            return new ResumenRemoto(respuesta[2], total, promedio, minimo, maximo);
        }

        // Solicita la informacion de ayuda y comandos soportados mediante el comando HELP
        public string Ayuda()
        {
            TramaCliente.Enviar(flujo, "HELP");
            string[] respuesta = LeerRespuesta();
            Exigir(respuesta, 6, "200", "HELP");

            if (respuesta[2] != "METEO/1.0" ||
                respuesta[3] != "512" ||
                respuesta[4] != "HISTORY_SEGUN_ESTACION")
            {
                throw new InvalidDataException("Parametros HELP invalidos");
            }

            string[] comandosEsperados =
            {
                "HELLO", "LIST", "CURRENT", "HISTORY", "SUMMARY", "HELP", "QUIT"
            };

            if (!respuesta[5].Split(',').SequenceEqual(comandosEsperados))
            {
                throw new InvalidDataException("Lista HELP no coincide con el protocolo");
            }

            return $"{respuesta[2]} | Maximo 512 bytes por linea | HISTORY depende de cada estacion | {respuesta[5]}";
        }

        // Envia el comando QUIT para finalizar ordenadamente la sesion remota
        public void Salir()
        {
            TramaCliente.Enviar(flujo, "QUIT");
            string[] respuesta = LeerRespuesta();
            Exigir(respuesta, 2, "200", "BYE");
        }

        // Lee una trama de respuesta y lanza ErrorServidorException si el servidor reporta un error
        private string[] LeerRespuesta()
        {
            string[] respuesta = TramaCliente.Leer(flujo);

            if (respuesta.Length > 0 &&
                int.TryParse(respuesta[0], NumberStyles.None, Cultura, out int codigo) &&
                codigo >= CODIGO_ERROR_MINIMO &&
                codigo <= CODIGO_ERROR_MAXIMO)
            {
                if (respuesta.Length != 3 || respuesta[1] != "ERROR" || string.IsNullOrWhiteSpace(respuesta[2]))
                {
                    throw new InvalidDataException("Formato de error no valido");
                }

                throw new ErrorServidorException(codigo, respuesta[2]);
            }

            return respuesta;
        }

        // Lee una respuesta multilinea compuesta por 210 COUNT, registros 211 y cierre 212 END
        private List<T> LeerMultiple<T>(
            string comando,
            string etiqueta,
            Func<string[], T> parsear,
            int? cantidadSolicitada = null)
        {
            string[] cabecera = LeerRespuesta();
            Exigir(cabecera, 3, "210", "COUNT");
            int cantidad = ConvertirEntero(cabecera[2], "COUNT");

            if (cantidad < 0)
            {
                throw new InvalidDataException("COUNT negativo");
            }

            if (cantidadSolicitada.HasValue && cantidad != cantidadSolicitada.Value)
            {
                throw new InvalidDataException($"{comando}: COUNT no coincide con la cantidad solicitada");
            }

            var registros = new List<T>();

            for (int i = 0; i < cantidad; i++)
            {
                string[] item;

                try
                {
                    item = LeerRespuesta();
                }
                catch (EndOfStreamException ex)
                {
                    throw new InvalidDataException($"{comando} incompleto: {i} de {cantidad} registros", ex);
                }

                if (item.Length == 2 && item[0] == "212" && item[1] == "END")
                {
                    throw new InvalidDataException($"{comando} incompleto: {i} de {cantidad} registros");
                }

                if (item.Length < 2 || item[0] != "211" || item[1] != etiqueta)
                {
                    throw new InvalidDataException($"{comando}: ITEM invalido en posicion {i + 1}");
                }

                registros.Add(parsear(item));
            }

            string[] fin;

            try
            {
                fin = LeerRespuesta();
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException($"{comando} incompleto: falta END", ex);
            }

            Exigir(fin, 2, "212", "END");

            return registros;
        }

        // Valida y construye un registro de estacion a partir de una linea 211 STATION
        private static EstacionRemota ParsearEstacion(string[] campos)
        {
            Exigir(campos, 5, "211", "STATION");
            ValidarCodigo(campos[2]);

            if (string.IsNullOrWhiteSpace(campos[3]))
            {
                throw new InvalidDataException("Nombre de estacion vacio");
            }

            if (!CiudadesValidas.Contains(campos[4]))
            {
                throw new InvalidDataException("Ciudad no reconocida");
            }

            return new EstacionRemota(campos[2], campos[3], campos[4]);
        }

        // Valida y construye un registro de lectura a partir de una linea 211 READING
        private static LecturaRemota ParsearLectura(string[] campos)
        {
            Exigir(campos, 6, "211", "READING");

            if (!DateTime.TryParseExact(
                    campos[2],
                    "yyyy-MM-ddTHH:mm:ss",
                    Cultura,
                    DateTimeStyles.None,
                    out DateTime fecha))
            {
                throw new InvalidDataException("Fecha de lectura invalida");
            }

            double temperatura = ConvertirNumero(campos[3], "TemperaturaCelsius");
            int humedad = ConvertirEntero(campos[4], "HumedadPorcentaje");

            if (humedad < HUMEDAD_MINIMA || humedad > HUMEDAD_MAXIMA)
            {
                throw new InvalidDataException("Humedad fuera de 0 a 100");
            }

            if (!EstadosTiempoValidos.Contains(campos[5]))
            {
                throw new InvalidDataException("EstadoTiempo desconocido");
            }

            return new LecturaRemota(fecha, temperatura, humedad, campos[5]);
        }

        // Verifica que la respuesta tenga el codigo, el tipo y la cantidad de campos esperados
        private static void Exigir(string[] campos, int totalCampos, string codigo, string tipo)
        {
            if (campos.Length != totalCampos || campos[0] != codigo || campos[1] != tipo)
            {
                throw new InvalidDataException(
                    $"Respuesta invalida: se esperaba {codigo}|{tipo} con {totalCampos} campos");
            }
        }

        // Verifica que el codigo de estacion recibido cumpla el patron EST###
        private static void ValidarCodigo(string codigo)
        {
            if (!Regex.IsMatch(codigo, @"^EST[0-9]{3}$", RegexOptions.CultureInvariant))
            {
                throw new InvalidDataException("Codigo de estacion invalido en respuesta");
            }
        }

        // Convierte un campo de texto en un numero entero validando su formato
        private static int ConvertirEntero(string valor, string nombreCampo)
        {
            if (!int.TryParse(valor, NumberStyles.Integer, Cultura, out int resultado))
            {
                throw new InvalidDataException($"Campo {nombreCampo} no es un entero valido");
            }

            return resultado;
        }

        // Convierte un campo de texto en un numero decimal finito validando su formato
        private static double ConvertirNumero(string valor, string nombreCampo)
        {
            if (!double.TryParse(valor, NumberStyles.Float, Cultura, out double resultado) ||
                double.IsNaN(resultado) ||
                double.IsInfinity(resultado))
            {
                throw new InvalidDataException($"Campo {nombreCampo} no es un numero valido");
            }

            return resultado;
        }
    }
}
