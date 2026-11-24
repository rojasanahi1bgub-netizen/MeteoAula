using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using ClienteTCP.Protocolo;

namespace ClienteTCP.Red
{
    // Gestiona la consola interactiva del cliente y la conexion TCP sincrona con el servidor
    public static class ClienteTcp
    {
        public const string IP_POR_DEFECTO = "127.0.0.1";
        public const int PUERTO_POR_DEFECTO = 5050;
        private const int TIEMPO_ESPERA_MS = 300000;
        private const int PUERTO_MINIMO = 1;
        private const int PUERTO_MAXIMO = 65535;
        private const string VERSION_POR_DEFECTO = "1.0";

        // Inicia la consola del cliente usando la entrada y salida estandar
        public static void Iniciar(string ip = IP_POR_DEFECTO, int puerto = PUERTO_POR_DEFECTO)
        {
            Iniciar(Console.In, Console.Out, ip, puerto);
        }

        // Ejecuta el bucle interactivo de comandos locales y consultas remotas METEO/1.0
        public static void Iniciar(
            TextReader entrada,
            TextWriter salida,
            string ip = IP_POR_DEFECTO,
            int puerto = PUERTO_POR_DEFECTO)
        {
            if (entrada == null)
            {
                throw new ArgumentNullException(nameof(entrada));
            }

            if (salida == null)
            {
                throw new ArgumentNullException(nameof(salida));
            }

            if (!DireccionValida(ip, puerto))
            {
                throw new ArgumentException("IP IPv4 o puerto inicial invalidos (1..65535)");
            }

            TcpClient? socket = null;
            NetworkStream? flujo = null;
            ClienteProtocolo? protocolo = null;
            bool identificado = false;
            bool terminar = false;

            // Cierra y libera los recursos de la conexion TCP local actual
            void Desconectar()
            {
                protocolo = null;
                identificado = false;
                flujo?.Dispose();
                flujo = null;
                socket?.Dispose();
                socket = null;
            }

            // Establece una nueva conexion TCP sincrona con el servidor configurado
            void Conectar()
            {
                if (socket != null)
                {
                    salida.WriteLine("Ya estas conectado. Usa DISCONNECT antes de CONNECT o RECONNECT.");
                    return;
                }

                var candidato = new TcpClient(AddressFamily.InterNetwork);

                try
                {
                    candidato.Connect(IPAddress.Parse(ip), puerto);
                    candidato.ReceiveTimeout = TIEMPO_ESPERA_MS;
                    candidato.SendTimeout = TIEMPO_ESPERA_MS;
                    NetworkStream nuevoFlujo = candidato.GetStream();
                    socket = candidato;
                    flujo = nuevoFlujo;
                    protocolo = new ClienteProtocolo(nuevoFlujo);
                    identificado = false;
                    salida.WriteLine($"Conectado a {ip}:{puerto}. Estado: CONECTADO (sin HELLO).");
                    salida.WriteLine("Antes de HELLO solo se admiten HELLO, HELP y QUIT.");
                }
                catch (SocketException ex)
                {
                    candidato.Dispose();
                    salida.WriteLine($"No se pudo conectar a {ip}:{puerto}: {ex.Message}");
                    salida.WriteLine("Puedes corregir la IP/puerto con CONFIG o intentar RECONNECT.");
                }
                catch (IOException ex)
                {
                    candidato.Dispose();
                    salida.WriteLine("Error al abrir la conexion: " + ex.Message);
                }
            }

            salida.WriteLine("MeteoAula - cliente TCP sincrono, METEO/1.0");
            salida.WriteLine($"Destino configurado: {ip}:{puerto} (IPv4)");
            salida.WriteLine("Escribe CONNECT para conectar; CONFIG <IP> <PUERTO> para cambiar el destino.");
            MostrarAyudaLocal(salida);

            try
            {
                while (!terminar)
                {
                    salida.Write(socket == null ? "meteo[sin conexion]> " : "meteo> ");
                    string? linea = entrada.ReadLine();

                    if (linea == null)
                    {
                        break;
                    }

                    string[] partes = linea.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    if (partes.Length == 0)
                    {
                        continue;
                    }

                    string comando = partes[0].ToUpperInvariant();

                    // Procesa los comandos locales de configuracion y apertura de conexion
                    if (comando == "CONFIG" || comando == "CONNECT")
                    {
                        if (socket != null)
                        {
                            salida.WriteLine("Ya hay una conexion activa. Usa DISCONNECT o RECONNECT.");
                            continue;
                        }

                        if ((comando == "CONFIG" && partes.Length != 3) ||
                            (comando == "CONNECT" && partes.Length != 1 && partes.Length != 3))
                        {
                            salida.WriteLine("Uso: CONFIG <IP> <PUERTO> o CONNECT [IP PUERTO]");
                            continue;
                        }

                        if (partes.Length == 3)
                        {
                            if (!int.TryParse(
                                    partes[2],
                                    NumberStyles.None,
                                    CultureInfo.InvariantCulture,
                                    out int nuevoPuerto) ||
                                !DireccionValida(partes[1], nuevoPuerto))
                            {
                                salida.WriteLine("IP o puerto invalidos. Usa IPv4 y puerto de 1 a 65535.");
                                continue;
                            }

                            ip = partes[1];
                            puerto = nuevoPuerto;
                            salida.WriteLine($"Destino configurado: {ip}:{puerto}");
                        }

                        if (comando == "CONNECT")
                        {
                            Conectar();
                        }

                        continue;
                    }

                    // Cierra la conexion actual si existe y abre una nueva sesion TCP
                    if (comando == "RECONNECT" && partes.Length == 1)
                    {
                        if (socket != null)
                        {
                            Desconectar();
                            salida.WriteLine("Conexion anterior cerrada.");
                        }

                        Conectar();
                        continue;
                    }

                    // Cierra la conexion TCP local sin salir de la aplicacion
                    if (comando == "DISCONNECT" && partes.Length == 1)
                    {
                        if (socket == null)
                        {
                            salida.WriteLine("No hay una conexion activa.");
                        }
                        else
                        {
                            Desconectar();
                            salida.WriteLine("Conexion local cerrada. Usa CONNECT o RECONNECT.");
                        }

                        continue;
                    }

                    // Muestra el estado actual de la conexion y el destino configurado
                    if (comando == "STATUS" && partes.Length == 1)
                    {
                        if (socket == null)
                        {
                            salida.WriteLine($"Sin conexion. Destino: {ip}:{puerto}");
                        }
                        else
                        {
                            string estadoActual = identificado ? "IDENTIFICADO / CONSULTAS" : "CONECTADO (sin HELLO)";
                            salida.WriteLine($"Conectado a {ip}:{puerto}. Estado local: {estadoActual}");
                        }

                        continue;
                    }

                    // Finaliza la ejecucion de la aplicacion cliente
                    if (comando == "EXIT" && partes.Length == 1)
                    {
                        terminar = true;
                        continue;
                    }

                    if (comando == "HELP" && socket == null && partes.Length == 1)
                    {
                        MostrarAyudaLocal(salida);
                        continue;
                    }

                    if (socket == null || protocolo == null)
                    {
                        salida.WriteLine("No estas conectado. Escribe CONNECT o CONNECT <IP> <PUERTO>.");
                        continue;
                    }

                    // Envia los comandos del protocolo METEO/1.0 al servidor y muestra la respuesta
                    try
                    {
                        if (comando == "HELLO" && partes.Length <= 2)
                        {
                            string version = partes.Length == 2 ? partes[1] : VERSION_POR_DEFECTO;
                            protocolo.Saludar(version);
                            identificado = true;
                            salida.WriteLine("Identificacion correcta: 200 WELCOME 1.0");
                        }
                        else if (partes.Length == 1 && comando.StartsWith("HELLO|", StringComparison.Ordinal))
                        {
                            string version = partes[0].Substring("HELLO|".Length);
                            protocolo.Saludar(version);
                            identificado = true;
                            salida.WriteLine("Identificacion correcta: 200 WELCOME 1.0");
                        }
                        else if (comando == "LIST" && partes.Length == 1)
                        {
                            foreach (EstacionRemota estacion in protocolo.Listar())
                            {
                                salida.WriteLine($"{estacion.Codigo}: {estacion.Nombre} ({estacion.Ciudad})");
                            }
                        }
                        else if (comando == "CURRENT" && partes.Length == 2)
                        {
                            LecturaActualRemota actual = protocolo.Actual(partes[1]);
                            MostrarLectura(salida, actual.Lectura, actual.CodigoEstacion);
                        }
                        else if (comando == "HISTORY" && partes.Length == 3)
                        {
                            if (!int.TryParse(
                                    partes[2],
                                    NumberStyles.Integer,
                                    CultureInfo.InvariantCulture,
                                    out int cantidad))
                            {
                                salida.WriteLine("La cantidad N debe ser un entero.");
                                continue;
                            }

                            foreach (LecturaRemota lectura in protocolo.Historial(partes[1], cantidad))
                            {
                                MostrarLectura(salida, lectura, partes[1]);
                            }
                        }
                        else if (comando == "SUMMARY" && partes.Length == 2)
                        {
                            ResumenRemoto resumen = protocolo.Resumen(partes[1]);
                            salida.WriteLine(
                                $"{resumen.CodigoEstacion}: {resumen.TotalLecturas} lecturas, " +
                                $"promedio {resumen.Promedio:0.##}, min {resumen.Minimo:0.##}, " +
                                $"max {resumen.Maximo:0.##} C");
                        }
                        else if (comando == "HELP" && partes.Length == 1)
                        {
                            salida.WriteLine(protocolo.Ayuda());
                            MostrarAyudaLocal(salida);
                        }
                        else if (comando == "QUIT" && partes.Length == 1)
                        {
                            protocolo.Salir();
                            Desconectar();
                            salida.WriteLine("Sesion finalizada (200 BYE). Usa CONNECT para otra sesion.");
                        }
                        else
                        {
                            salida.WriteLine("Comando invalido en consola. Usa HELP para ver la sintaxis.");
                        }
                    }
                    catch (ErrorServidorException ex)
                    {
                        salida.WriteLine($"Error del protocolo {ex.Codigo}: " + ExplicarError(ex.Codigo, ex.Message));

                        if (ex.Codigo == 428 && !identificado)
                        {
                            salida.WriteLine("Primero escribe HELLO para identificarte.");
                        }
                    }
                    catch (InvalidDataException ex)
                    {
                        salida.WriteLine("Mensaje invalido: " + ex.Message);
                        Desconectar();
                        salida.WriteLine("Conexion cerrada por seguridad. Usa RECONNECT.");
                    }
                    catch (IOException ex)
                    {
                        salida.WriteLine("Servidor desconectado o tiempo de espera agotado: " + ex.Message);
                        Desconectar();
                        salida.WriteLine("Puedes intentar RECONNECT manualmente.");
                    }
                    catch (SocketException ex)
                    {
                        salida.WriteLine("Fallo de conexion con el servidor: " + ex.Message);
                        Desconectar();
                        salida.WriteLine("Puedes intentar RECONNECT manualmente.");
                    }
                    catch (ObjectDisposedException)
                    {
                        Desconectar();
                        salida.WriteLine("La conexion se cerro. Puedes intentar RECONNECT.");
                    }
                }
            }
            finally
            {
                Desconectar();
                salida.WriteLine("Cliente finalizado.");
            }
        }

        // Verifica que la direccion IP sea IPv4 valida y el puerto este entre 1 y 65535
        private static bool DireccionValida(string ip, int puerto)
        {
            return puerto >= PUERTO_MINIMO &&
                   puerto <= PUERTO_MAXIMO &&
                   IPAddress.TryParse(ip, out IPAddress? direccion) &&
                   direccion.AddressFamily == AddressFamily.InterNetwork;
        }

        // Traduce el codigo numerico de error del protocolo a un mensaje descriptivo para el usuario
        private static string ExplicarError(int codigo, string mensaje)
        {
            return codigo switch
            {
                400 => "Comando desconocido (" + mensaje + ")",
                401 => "Falta un parametro (" + mensaje + ")",
                402 => "Formato de solicitud invalido (" + mensaje + ")",
                404 => "La estacion solicitada no existe (" + mensaje + ")",
                409 => "Operacion no permitida en este estado (" + mensaje + ")",
                413 => "Mensaje de mas de 512 bytes (" + mensaje + ")",
                416 => "Cantidad fuera del rango disponible (" + mensaje + ")",
                428 => "Debes ejecutar HELLO antes de consultar (" + mensaje + ")",
                505 => "Version incompatible; usa 1.0 (" + mensaje + ")",
                500 => "Error interno del servidor (" + mensaje + ")",
                _ => mensaje
            };
        }

        // Muestra en consola los comandos locales y remotos disponibles
        private static void MostrarAyudaLocal(TextWriter salida)
        {
            salida.WriteLine(
                "Comandos locales: CONFIG <IP> <PUERTO>, CONNECT [IP PUERTO], " +
                "RECONNECT, DISCONNECT, STATUS, EXIT.");
            salida.WriteLine(
                "Comandos METEO/1.0 conectado: HELLO, LIST, CURRENT EST001, " +
                "HISTORY EST001 2, SUMMARY EST001, HELP, QUIT.");
            salida.WriteLine("QUIT cierra la sesion remota; EXIT cierra la aplicacion cliente.");
        }

        // Formatea e imprime en consola los datos de una lectura meteorologica
        private static void MostrarLectura(TextWriter salida, LecturaRemota lectura, string codigo)
        {
            salida.WriteLine(
                $"{codigo} | {lectura.FechaHora:yyyy-MM-dd HH:mm:ss} | " +
                $"{lectura.TemperaturaCelsius:0.##} C | {lectura.HumedadPorcentaje}% | {lectura.Estado}");
        }
    }
}
