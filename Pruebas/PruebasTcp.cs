using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServidorTCP.Protocolo;
using ServidorTCP.Red;

// Ejecuta pruebas de integracion de extremo a extremo sobre sockets TCP reales en localhost
internal static class PruebasTcp
{
    private const int TIEMPO_ESPERA_SERVIDOR_MS = 1200;
    private const int TIEMPO_ESPERA_CLIENTE_MS = 4000;
    private const int TIEMPO_INACTIVIDAD_PRUEBA_MS = 1600;
    private const int TIEMPO_ARRANQUE_MAXIMO_MS = 6000;
    private const int TIEMPO_CIERRE_MAXIMO_MS = 5000;

    // Levanta un servidor TCP en un puerto libre y verifica los flujos del protocolo
    public static void Ejecutar(Action<bool, string, string, string> verificar)
    {
        using var cancelar = new CancellationTokenSource();
        var servidor = new ServidorTcp(TIEMPO_ESPERA_SERVIDOR_MS);
        Task tarea = Task.Run(() => servidor.Iniciar(0, cancelar.Token));

        try
        {
            bool disponible = SpinWait.SpinUntil(
                () => servidor.PuertoActual != 0 || tarea.IsCompleted,
                TIEMPO_ARRANQUE_MAXIMO_MS);

            verificar(
                disponible && servidor.PuertoActual > 0,
                "TCP servidor inicia en puerto libre",
                "Puerto activo > 0",
                "Puerto=" + servidor.PuertoActual);

            int puerto = servidor.PuertoActual;

            // Verifica comandos validos, errores de protocolo y cierre con QUIT en una sesion TCP
            using (TcpClient tcp = Conectar(puerto))
            using (NetworkStream flujo = tcp.GetStream())
            {
                TramaProtocolo.Escribir(flujo, "LIST");
                string respuesta = Recibir(flujo);
                verificar(
                    respuesta == "428|ERROR|HELLO_REQUERIDO",
                    "TCP LIST antes de HELLO se rechaza",
                    "428|ERROR|HELLO_REQUERIDO",
                    respuesta);

                TramaProtocolo.Escribir(flujo, "HELP");
                respuesta = Recibir(flujo);
                verificar(
                    respuesta.StartsWith("200|HELP|METEO/1.0|512|", StringComparison.Ordinal),
                    "TCP HELP antes de HELLO funciona",
                    "200|HELP|METEO/1.0|512|...",
                    respuesta);

                TramaProtocolo.Escribir(flujo, "HELLO|1.0");
                respuesta = Recibir(flujo);
                verificar(
                    respuesta == "200|WELCOME|1.0",
                    "TCP HELLO identifica",
                    "200|WELCOME|1.0",
                    respuesta);

                TramaProtocolo.Escribir(flujo, "CURRENT|EST999");
                respuesta = Recibir(flujo);
                verificar(
                    respuesta == "404|ERROR|ESTACION_INEXISTENTE",
                    "TCP error de estacion inexistente",
                    "404|ERROR|ESTACION_INEXISTENTE",
                    respuesta);

                TramaProtocolo.Escribir(flujo, "HISTORY|EST003|15");
                respuesta = Recibir(flujo);
                verificar(
                    respuesta == "416|ERROR|CANTIDAD_FUERA_DE_RANGO",
                    "TCP HISTORY fuera de rango",
                    "416|ERROR|CANTIDAD_FUERA_DE_RANGO",
                    respuesta);

                TramaProtocolo.Escribir(flujo, "OTRO");
                respuesta = Recibir(flujo);
                verificar(
                    respuesta == "400|ERROR|COMANDO_DESCONOCIDO",
                    "TCP comando desconocido",
                    "400|ERROR|COMANDO_DESCONOCIDO",
                    respuesta);

                TramaProtocolo.Escribir(flujo, "HISTORY|EST002");
                respuesta = Recibir(flujo);
                verificar(
                    respuesta == "401|ERROR|PARAMETRO_FALTANTE",
                    "TCP parametro faltante",
                    "401|ERROR|PARAMETRO_FALTANTE",
                    respuesta);

                TramaProtocolo.Escribir(flujo, "HISTORY|EST002|abc");
                respuesta = Recibir(flujo);
                verificar(
                    respuesta == "402|ERROR|CANTIDAD_NO_ENTERA",
                    "TCP formato invalido",
                    "402|ERROR|CANTIDAD_NO_ENTERA",
                    respuesta);

                TramaProtocolo.Escribir(flujo, "HISTORY|EST002|20");
                string cabecera = Recibir(flujo);
                int items = 0;

                for (int i = 0; i < 20; i++)
                {
                    string registro = Recibir(flujo);

                    if (registro.StartsWith("211|READING|", StringComparison.Ordinal))
                    {
                        items++;
                    }
                }

                string fin = Recibir(flujo);
                verificar(
                    cabecera == "210|COUNT|20" && items == 20 && fin == "212|END",
                    "TCP HISTORY sin limite global y COUNT+END",
                    "210|COUNT|20, 20 ITEM, 212|END",
                    cabecera + ", " + items + " ITEM, " + fin);

                TramaProtocolo.Escribir(flujo, "QUIT");
                respuesta = Recibir(flujo);
                verificar(respuesta == "200|BYE", "TCP QUIT responde BYE", "200|BYE", respuesta);

                int cierre = flujo.ReadByte();
                verificar(cierre == -1, "TCP QUIT cierra el socket", "EOF=-1", "EOF=" + cierre);
            }

            // Verifica que el servidor rechace tramas con UTF-8 invalido y aisle la sesion
            using (TcpClient tcp = Conectar(puerto))
            using (NetworkStream flujo = tcp.GetStream())
            {
                byte[] bytes = { 0xC3, 0x28, 0x0A };
                flujo.Write(bytes, 0, bytes.Length);
                string respuesta = Recibir(flujo);
                verificar(
                    respuesta == "402|ERROR|UTF8_INVALIDO",
                    "TCP UTF-8 invalido: error y sesion aislada",
                    "402|ERROR|UTF8_INVALIDO",
                    respuesta);
            }

            // Verifica que el servidor rechace mensajes mayores a 512 bytes
            using (TcpClient tcp = Conectar(puerto))
            using (NetworkStream flujo = tcp.GetStream())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(new string('A', 512) + "\n");
                flujo.Write(bytes, 0, bytes.Length);
                string respuesta = Recibir(flujo);
                verificar(
                    respuesta == "413|ERROR|MENSAJE_DEMASIADO_GRANDE",
                    "TCP mensaje de 513 bytes: error y sesion aislada",
                    "413|ERROR|MENSAJE_DEMASIADO_GRANDE",
                    respuesta);
            }

            // Simula una desconexion brusca a mitad de trama para comprobar que el servidor sigue activo
            using (TcpClient tcp = Conectar(puerto))
            {
                tcp.LingerState = new LingerOption(true, 0);
                NetworkStream flujo = tcp.GetStream();
                byte[] parcial = Encoding.UTF8.GetBytes("HISTORY|EST001|");
                flujo.Write(parcial, 0, parcial.Length);
            }

            using (TcpClient tcp = Conectar(puerto))
            using (NetworkStream flujo = tcp.GetStream())
            {
                TramaProtocolo.Escribir(flujo, "HELLO|1.0");
                string respuesta = Recibir(flujo);
                verificar(
                    respuesta == "200|WELCOME|1.0",
                    "TCP reconecta tras desconexion inesperada",
                    "200|WELCOME|1.0",
                    respuesta);
            }

            // Verifica que un cliente inactivo sea desconectado al agotarse el tiempo de espera
            using (TcpClient tcp = Conectar(puerto))
            using (NetworkStream flujo = tcp.GetStream())
            {
                Thread.Sleep(TIEMPO_INACTIVIDAD_PRUEBA_MS);
                bool desconectado;

                try
                {
                    desconectado = flujo.ReadByte() == -1;
                }
                catch (IOException)
                {
                    desconectado = true;
                }

                verificar(
                    desconectado,
                    "TCP cliente inactivo se desconecta",
                    "EOF o error de socket tras timeout",
                    desconectado ? "Desconectado" : "Sigue conectado");
            }
        }
        finally
        {
            cancelar.Cancel();

            try
            {
                tarea.Wait(TIEMPO_CIERRE_MAXIMO_MS);
            }
            catch (AggregateException)
            {
                // Ignora la excepcion de tarea si ya fue registrada por una verificacion previa
            }

            verificar(
                tarea.IsCompleted && !tarea.IsFaulted,
                "TCP cierre ordenado del listener",
                "Servidor finalizado sin error",
                "Completed=" + tarea.IsCompleted + ", Faulted=" + tarea.IsFaulted);
        }
    }

    // Abre un cliente TCP conectado al puerto local de pruebas
    private static TcpClient Conectar(int puerto)
    {
        var tcp = new TcpClient();
        tcp.ReceiveTimeout = TIEMPO_ESPERA_CLIENTE_MS;
        tcp.SendTimeout = TIEMPO_ESPERA_CLIENTE_MS;
        tcp.Connect(IPAddress.Loopback, puerto);

        return tcp;
    }

    // Lee una linea del servidor o lanza excepcion si la conexion se cerro
    private static string Recibir(Stream flujo)
    {
        string? linea = TramaProtocolo.Leer(flujo);

        if (linea == null)
        {
            throw new EndOfStreamException("Servidor cerro antes de responder");
        }

        return linea;
    }
}
