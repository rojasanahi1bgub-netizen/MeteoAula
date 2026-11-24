using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ServidorTCP.Protocolo;
using ServidorTCP.Servicios;

namespace ServidorTCP.Red
{
    // Gestiona la escucha de conexiones TCP entrantes y la atencion secuencial de clientes
    public sealed class ServidorTcp
    {
        public const int PUERTO_POR_DEFECTO = 5050;
        public const int TIEMPO_ESPERA_DEFECTO_MS = 300000;
        private const int PUERTO_MINIMO = 0;
        private const int PUERTO_MAXIMO = 65535;

        private readonly ServicioConsultas servicio = new ServicioConsultas();
        private readonly int tiempoEsperaMilisegundos;
        private readonly object sincronizacion = new object();
        private readonly Action<string> registrar;
        private TcpListener? listener;
        private int puertoActual;

        public int PuertoActual
        {
            get
            {
                return Volatile.Read(ref puertoActual);
            }
        }

        // Inicializa el servidor TCP con el tiempo maximo de inactividad y la salida de registro
        public ServidorTcp(int tiempoEsperaMilisegundos = TIEMPO_ESPERA_DEFECTO_MS, Action<string>? registrar = null)
        {
            if (tiempoEsperaMilisegundos <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tiempoEsperaMilisegundos));
            }

            this.tiempoEsperaMilisegundos = tiempoEsperaMilisegundos;
            this.registrar = registrar ?? Console.WriteLine;
        }

        // Inicia el listener TCP y acepta conexiones de clientes de forma sincrona
        public void Iniciar(
            int puerto = PUERTO_POR_DEFECTO,
            CancellationToken cancelacion = default,
            IPAddress? direccionEscucha = null)
        {
            if (puerto < PUERTO_MINIMO || puerto > PUERTO_MAXIMO)
            {
                throw new ArgumentOutOfRangeException(nameof(puerto));
            }

            IPAddress direccion = direccionEscucha ?? IPAddress.Loopback;

            if (direccion.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException("La IP de escucha debe ser IPv4", nameof(direccionEscucha));
            }

            var nuevoListener = new TcpListener(direccion, puerto);

            lock (sincronizacion)
            {
                if (listener != null)
                {
                    throw new InvalidOperationException("El servidor ya esta iniciado");
                }

                listener = nuevoListener;
            }

            try
            {
                nuevoListener.Start();
                Volatile.Write(ref puertoActual, ((IPEndPoint)nuevoListener.LocalEndpoint).Port);
                Registrar($"ESCUCHANDO METEO/1.0 en {direccion}:{PuertoActual}");

                // Registra la detencion del listener al solicitarse la cancelacion
                using CancellationTokenRegistration registro = cancelacion.Register(() => nuevoListener.Stop());

                while (!cancelacion.IsCancellationRequested)
                {
                    TcpClient cliente;

                    try
                    {
                        cliente = nuevoListener.AcceptTcpClient();
                    }
                    catch (SocketException) when (cancelacion.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (ObjectDisposedException) when (cancelacion.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (SocketException ex)
                    {
                        Console.Error.WriteLine("Error al aceptar un cliente: " + ex.Message);
                        continue;
                    }

                    // Aisla la sesion del cliente para que un fallo no detenga el servidor
                    using (cliente)
                    {
                        string direccionRemota = cliente.Client.RemoteEndPoint?.ToString() ?? "desconocido";
                        using CancellationTokenRegistration cierreCliente = cancelacion.Register(() => cliente.Dispose());

                        try
                        {
                            cliente.ReceiveTimeout = tiempoEsperaMilisegundos;
                            cliente.SendTimeout = tiempoEsperaMilisegundos;
                            Registrar($"CONEXION ABIERTA: {direccionRemota}");
                            Atender(cliente);
                        }
                        catch (IOException ex)
                        {
                            Console.Error.WriteLine("Error de entrada/salida del cliente: " + ex.Message);
                        }
                        catch (SocketException ex)
                        {
                            Console.Error.WriteLine("Error de socket del cliente: " + ex.Message);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("Error inesperado de sesion: " + ex.Message);
                        }
                        finally
                        {
                            Registrar("CONEXION CERRADA: " + direccionRemota);
                        }
                    }
                }
            }
            finally
            {
                // Libera el puerto de escucha al finalizar o cancelar la ejecucion
                nuevoListener.Stop();
                Volatile.Write(ref puertoActual, 0);

                lock (sincronizacion)
                {
                    if (ReferenceEquals(listener, nuevoListener))
                    {
                        listener = null;
                    }
                }

                Registrar("Servidor detenido limpiamente");
            }
        }

        // Atiende las solicitudes de un cliente conectado hasta que cierre sesion o se desconecte
        private void Atender(TcpClient cliente)
        {
            var interprete = new InterpreteProtocolo(servicio);
            using NetworkStream flujo = cliente.GetStream();

            while (interprete.Estado != EstadoSesion.CERRADO)
            {
                try
                {
                    string? linea = TramaProtocolo.Leer(flujo);

                    if (linea == null)
                    {
                        return;
                    }

                    Registrar("COMANDO RECIBIDO: " + linea);
                    ResultadoProtocolo respuesta = interprete.ProcesarLinea(linea);

                    foreach (string salida in respuesta.Lineas)
                    {
                        TramaProtocolo.Escribir(flujo, salida);
                    }

                    if (respuesta.Lineas.Count > 0)
                    {
                        Registrar("RESPUESTA ENVIADA: " + respuesta.Lineas[0].Split('|')[0]);
                    }

                    if (respuesta.CerrarConexion)
                    {
                        return;
                    }
                }
                catch (ExcepcionProtocolo ex)
                {
                    Registrar($"ERROR DE TRAMA RECIBIDA: {ex.Codigo} {ex.Message}");
                    IntentarError(flujo, ex.Codigo, ex.Message);
                    return;
                }
                catch (IOException ex)
                {
                    Console.Error.WriteLine("Desconexion o timeout: " + ex.Message);
                    return;
                }
                catch (SocketException ex)
                {
                    Console.Error.WriteLine("Desconexion de socket: " + ex.Message);
                    return;
                }
            }
        }

        // Registra un evento del servidor con marca de tiempo
        private void Registrar(string mensaje)
        {
            registrar($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {mensaje}");
        }

        // Intenta enviar una trama de error al cliente antes de cerrar una conexion con fallo de encuadre
        private static void IntentarError(Stream flujo, int codigo, string mensaje)
        {
            try
            {
                string lineaError = TramaProtocolo.CrearLinea(
                    codigo.ToString(CultureInfo.InvariantCulture),
                    "ERROR",
                    mensaje);
                TramaProtocolo.Escribir(flujo, lineaError);
            }
            catch (IOException)
            {
                // Ignora el fallo si el cliente ya cerro el flujo
            }
            catch (SocketException)
            {
                // Ignora el fallo si el socket ya fue desconectado
            }
            catch (ObjectDisposedException)
            {
                // Ignora el fallo si el flujo ya fue liberado
            }
        }
    }
}
