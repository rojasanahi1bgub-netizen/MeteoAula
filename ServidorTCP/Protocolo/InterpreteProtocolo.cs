using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ServidorTCP.Modelos;
using ServidorTCP.Servicios;

namespace ServidorTCP.Protocolo
{
    // Estados posibles de la sesion de un cliente en el protocolo METEO/1.0
    public enum EstadoSesion
    {
        CONECTADO,
        IDENTIFICADO,
        CONSULTAS,
        CERRADO
    }

    // Contiene las lineas de respuesta generadas y la indicacion de cierre de sesion
    public sealed class ResultadoProtocolo
    {
        public IReadOnlyList<string> Lineas { get; }
        public bool CerrarConexion { get; }

        // Inicializa el resultado con las lineas a transmitir y si debe cerrarse la conexion
        public ResultadoProtocolo(IReadOnlyList<string> lineas, bool cerrarConexion = false)
        {
            Lineas = lineas;
            CerrarConexion = cerrarConexion;
        }
    }

    // Interpreta los comandos del protocolo METEO/1.0, controla el estado de sesion y ejecuta consultas
    public sealed class InterpreteProtocolo
    {
        private static readonly CultureInfo Cultura = CultureInfo.InvariantCulture;
        private static readonly Regex PatronCodigo = new Regex(
            @"^EST[0-9]{3}$",
            RegexOptions.CultureInvariant);
        private static readonly string[] ComandosValidos =
        {
            "HELLO", "LIST", "CURRENT", "HISTORY", "SUMMARY", "HELP", "QUIT"
        };

        private readonly ServicioConsultas servicio;

        public EstadoSesion Estado { get; private set; } = EstadoSesion.CONECTADO;

        // Inicializa el interprete de protocolo para una sesion de cliente
        public InterpreteProtocolo(ServicioConsultas servicioConsultas)
        {
            if (servicioConsultas == null)
            {
                throw new ArgumentNullException(nameof(servicioConsultas));
            }

            servicio = servicioConsultas;
        }

        // Procesa una linea de solicitud recibida y devuelve las tramas de respuesta correspondientes
        public ResultadoProtocolo ProcesarLinea(string solicitud)
        {
            if (Estado == EstadoSesion.CERRADO)
            {
                return Unica("409", "ERROR", "SESION_CERRADA");
            }

            try
            {
                TramaProtocolo.ValidarLinea(solicitud);
                string[] campos = solicitud.Split('|');
                string comando = campos[0];

                if (!ComandosValidos.Contains(comando, StringComparer.Ordinal))
                {
                    throw new ExcepcionProtocolo(400, "COMANDO_DESCONOCIDO");
                }

                // Exige identificacion previa con HELLO antes de permitir consultas
                if (Estado == EstadoSesion.CONECTADO &&
                    comando != "HELLO" &&
                    comando != "HELP" &&
                    comando != "QUIT")
                {
                    throw new ExcepcionProtocolo(428, "HELLO_REQUERIDO");
                }

                switch (comando)
                {
                    case "HELLO":
                    {
                        VerificarParametros(campos, 2);

                        if (Estado != EstadoSesion.CONECTADO)
                        {
                            throw new ExcepcionProtocolo(409, "HELLO_YA_EJECUTADO");
                        }

                        if (campos[1] != EspecificacionMeteo.VERSION_PROTOCOLO)
                        {
                            throw new ExcepcionProtocolo(505, "VERSION_INCOMPATIBLE");
                        }

                        Estado = EstadoSesion.IDENTIFICADO;

                        return Unica("200", "WELCOME", EspecificacionMeteo.VERSION_PROTOCOLO);
                    }

                    case "HELP":
                    {
                        VerificarParametros(campos, 1);

                        return Unica(
                            "200",
                            "HELP",
                            EspecificacionMeteo.NOMBRE_PROTOCOLO,
                            EspecificacionMeteo.MAX_BYTES_POR_LINEA.ToString(Cultura),
                            "HISTORY_SEGUN_ESTACION",
                            "HELLO,LIST,CURRENT,HISTORY,SUMMARY,HELP,QUIT");
                    }

                    case "QUIT":
                    {
                        VerificarParametros(campos, 1);
                        Estado = EstadoSesion.CERRADO;

                        return UnicaCerrar("200", "BYE");
                    }

                    case "LIST":
                    {
                        VerificarParametros(campos, 1);
                        IReadOnlyList<Estacion> estaciones = servicio.ListarEstaciones();

                        List<string> registros = estaciones.Select(estacion => TramaProtocolo.CrearLinea(
                            "211",
                            "STATION",
                            estacion.Codigo,
                            estacion.Nombre,
                            estacion.Ciudad.ToString())).ToList();

                        ResultadoProtocolo resultado = Multiple(registros);
                        Estado = EstadoSesion.CONSULTAS;

                        return resultado;
                    }

                    case "CURRENT":
                    {
                        VerificarParametros(campos, 2);
                        string codigo = ObtenerCodigo(campos[1]);
                        Lectura lectura = servicio.ObtenerLecturaMasReciente(codigo);

                        ResultadoProtocolo resultado = Unica(
                            "200",
                            "CURRENT",
                            codigo,
                            FormatearFecha(lectura.FechaHora),
                            FormatearNumero(lectura.TemperaturaCelsius),
                            lectura.HumedadPorcentaje.ToString(Cultura),
                            lectura.Estado.ToString());

                        Estado = EstadoSesion.CONSULTAS;

                        return resultado;
                    }

                    case "HISTORY":
                    {
                        VerificarParametros(campos, 3);
                        string codigo = ObtenerCodigo(campos[1]);
                        int cantidad = ObtenerEntero(campos[2]);
                        IReadOnlyList<Lectura> lecturas = servicio.ObtenerHistorial(codigo, cantidad);

                        List<string> registros = lecturas.Select(lectura => TramaProtocolo.CrearLinea(
                            "211",
                            "READING",
                            FormatearFecha(lectura.FechaHora),
                            FormatearNumero(lectura.TemperaturaCelsius),
                            lectura.HumedadPorcentaje.ToString(Cultura),
                            lectura.Estado.ToString())).ToList();

                        ResultadoProtocolo resultado = Multiple(registros);
                        Estado = EstadoSesion.CONSULTAS;

                        return resultado;
                    }

                    case "SUMMARY":
                    {
                        VerificarParametros(campos, 2);
                        string codigo = ObtenerCodigo(campos[1]);
                        ResumenTemperatura resumen = servicio.ObtenerResumenTemperatura(codigo);

                        ResultadoProtocolo resultado = Unica(
                            "200",
                            "SUMMARY",
                            resumen.CodigoEstacion,
                            resumen.TotalLecturas.ToString(Cultura),
                            FormatearNumero(resumen.Promedio),
                            FormatearNumero(resumen.Minimo),
                            FormatearNumero(resumen.Maximo));

                        Estado = EstadoSesion.CONSULTAS;

                        return resultado;
                    }

                    default:
                    {
                        throw new ExcepcionProtocolo(400, "COMANDO_DESCONOCIDO");
                    }
                }
            }
            catch (ExcepcionProtocolo ex)
            {
                return Unica(ex.Codigo.ToString(Cultura), "ERROR", ex.Message);
            }
            catch (KeyNotFoundException)
            {
                return Unica("404", "ERROR", "ESTACION_INEXISTENTE");
            }
            catch (ArgumentOutOfRangeException)
            {
                return Unica("416", "ERROR", "CANTIDAD_FUERA_DE_RANGO");
            }
            catch (Exception)
            {
                return Unica("500", "ERROR", "ERROR_INTERNO");
            }
        }

        // Normaliza y valida el formato EST### del codigo de estacion
        private static string ObtenerCodigo(string valor)
        {
            string codigo = valor.Trim().ToUpperInvariant();

            if (!PatronCodigo.IsMatch(codigo))
            {
                throw new ExcepcionProtocolo(402, "CODIGO_ESTACION_INVALIDO");
            }

            return codigo;
        }

        // Convierte un texto en entero validando formato y rango numerico
        private static int ObtenerEntero(string valor)
        {
            if (!Regex.IsMatch(valor, @"^[+-]?[0-9]+$", RegexOptions.CultureInvariant))
            {
                throw new ExcepcionProtocolo(402, "CANTIDAD_NO_ENTERA");
            }

            if (!int.TryParse(valor, NumberStyles.AllowLeadingSign, Cultura, out int numero))
            {
                throw new ExcepcionProtocolo(416, "CANTIDAD_FUERA_DE_RANGO");
            }

            return numero;
        }

        // Valida que la cantidad de campos coincida con lo esperado y no existan parametros vacios
        private static void VerificarParametros(string[] campos, int cantidadEsperada)
        {
            if (campos.Length < cantidadEsperada)
            {
                throw new ExcepcionProtocolo(401, "PARAMETRO_FALTANTE");
            }

            if (campos.Length > cantidadEsperada)
            {
                throw new ExcepcionProtocolo(402, "CANTIDAD_DE_CAMPOS_INVALIDA");
            }

            for (int i = 1; i < campos.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(campos[i]))
                {
                    throw new ExcepcionProtocolo(401, "PARAMETRO_FALTANTE");
                }
            }
        }

        // Formatea una fecha en formato estandar ISO-8601
        private static string FormatearFecha(DateTime valor)
        {
            return valor.ToString("yyyy-MM-ddTHH:mm:ss", Cultura);
        }

        // Formatea un valor decimal con hasta dos decimales y punto invariante
        private static string FormatearNumero(double valor)
        {
            return valor.ToString("0.##", Cultura);
        }

        // Construye un resultado de protocolo de una sola linea
        private static ResultadoProtocolo Unica(params string[] campos)
        {
            return new ResultadoProtocolo(new[] { TramaProtocolo.CrearLinea(campos) });
        }

        // Construye un resultado de una sola linea indicando el cierre de la conexion
        private static ResultadoProtocolo UnicaCerrar(params string[] campos)
        {
            return new ResultadoProtocolo(new[] { TramaProtocolo.CrearLinea(campos) }, true);
        }

        // Construye una respuesta multilinea con cabecera 210 COUNT, registros 211 y cierre 212 END
        private static ResultadoProtocolo Multiple(List<string> registros)
        {
            var lineas = new List<string>
            {
                TramaProtocolo.CrearLinea("210", "COUNT", registros.Count.ToString(Cultura))
            };
            lineas.AddRange(registros);
            lineas.Add(TramaProtocolo.CrearLinea("212", "END"));

            return new ResultadoProtocolo(lineas);
        }
    }
}
