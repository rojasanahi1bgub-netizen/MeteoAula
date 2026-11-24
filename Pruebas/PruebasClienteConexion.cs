using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ClienteTCP.Red;
using ServidorTCP.Red;

// Verifica el flujo completo de la consola del cliente conectandose a un servidor TCP local
internal static class PruebasClienteConexion
{
    private const int TIEMPO_ESPERA_MS = 5000;
    private const int TIEMPO_REGISTRO_LOG_MS = 3000;

    // Ejecuta una secuencia simulada de comandos de usuario sobre ClienteTcp y valida las salidas
    public static void Ejecutar(Action<bool, string, string, string> verificar)
    {
        var registros = new ConcurrentQueue<string>();
        var servidor = new ServidorTcp(TIEMPO_ESPERA_MS, linea => registros.Enqueue(linea));
        using var cancelacion = new CancellationTokenSource();
        Task tarea = Task.Run(() => servidor.Iniciar(0, cancelacion.Token));

        try
        {
            bool arrancado = SpinWait.SpinUntil(
                () => servidor.PuertoActual > 0 || tarea.IsCompleted,
                TIEMPO_ESPERA_MS);

            verificar(
                arrancado && servidor.PuertoActual > 0,
                "3.a servidor disponible para configurar IP/puerto",
                "Puerto de prueba asignado",
                "Puerto: " + servidor.PuertoActual);

            if (!arrancado || servidor.PuertoActual == 0)
            {
                return;
            }

            int puerto = servidor.PuertoActual;

            // Secuencia de comandos locales y remotos para validar configuracion, consultas y reconexion
            string comandos =
                "CONFIG 300.1.1.1 5050\n" +
                "CONFIG 127.0.0.1 70000\n" +
                $"CONFIG 127.0.0.1 {puerto}\n" +
                "STATUS\nCONNECT\nLIST\nHELLO\nLIST\n" +
                "CURRENT EST001\nHISTORY EST003 2\nSUMMARY EST001\nQUIT\n" +
                "RECONNECT\nLIST\nHELLO\nLIST\nDISCONNECT\nSTATUS\nEXIT\n";

            using var entradas = new StringReader(comandos);
            using var salidas = new StringWriter();
            ClienteTcp.Iniciar(entradas, salidas);
            string texto = salidas.ToString();

            verificar(
                texto.Contains("IP o puerto invalidos", StringComparison.Ordinal),
                "3.a configuraciones invalidas se rechazan",
                "Mensajes claros para IP y puerto",
                "Rechazo de configuracion: " + texto.Contains("IP o puerto invalidos"));

            verificar(
                texto.Contains($"Destino configurado: 127.0.0.1:{puerto}", StringComparison.Ordinal),
                "3.a usuario configura IPv4 y puerto",
                $"127.0.0.1:{puerto}",
                "Configurado");

            verificar(
                texto.Contains($"Conectado a 127.0.0.1:{puerto}", StringComparison.Ordinal),
                "3.a se establece conexion TCP manual",
                "Conectado a IP:puerto",
                "Conexion establecida");

            verificar(
                texto.Contains("Error del protocolo 428", StringComparison.Ordinal),
                "3.b error HELLO requerido comprensible",
                "Codigo 428 explicado",
                "Mostrado=" + texto.Contains("Error del protocolo 428"));

            verificar(
                texto.Contains("Identificacion correcta: 200 WELCOME 1.0", StringComparison.Ordinal),
                "3.a HELLO manual tras CONNECT",
                "200 WELCOME",
                "HELLO aceptado");

            verificar(
                texto.Contains("EST001: Quito Centro (UIO)", StringComparison.Ordinal) &&
                texto.Contains("EST005: Manta Costa (MEC)", StringComparison.Ordinal),
                "3.a LIST muestra resultados estructurados",
                "EST001 y EST005 con campos",
                "Estaciones renderizadas");

            verificar(
                texto.Contains("ParcialmenteNublado", StringComparison.Ordinal) &&
                texto.Contains("15 lecturas", StringComparison.Ordinal),
                "3.a CURRENT HISTORY SUMMARY se interpretan",
                "Datos estructurados",
                "Datos visibles");

            verificar(
                texto.Contains("Sesion finalizada (200 BYE)", StringComparison.Ordinal) &&
                texto.Contains("Conexion local cerrada", StringComparison.Ordinal),
                "3.b QUIT y DISCONNECT cierran la sesion",
                "Cierre confirmado",
                "Cierres anunciados");

            verificar(
                texto.Split("Conectado a 127.0.0.1:").Length >= 3,
                "3.b RECONNECT abre segunda conexion bajo orden manual",
                "Dos conexiones distintas",
                "Conexiones en la salida=" + (texto.Split("Conectado a 127.0.0.1:").Length - 1));

            verificar(
                texto.Split("Error del protocolo 428").Length == 3,
                "3.b despues de reconectar se necesita otro HELLO",
                "428 en primera y segunda sesion",
                "428 vistos=" + (texto.Split("Error del protocolo 428").Length - 1));

            verificar(
                texto.Contains("Sin conexion. Destino:", StringComparison.Ordinal) &&
                texto.Contains("Cliente finalizado", StringComparison.Ordinal),
                "3.b vuelve al menu tras QUIT y DISCONNECT",
                "Menu sigue disponible",
                "STATUS/EXIT funcionales");

            // Espera a que el servidor registre el cierre de las dos sesiones en la cola de logs
            SpinWait.SpinUntil(
                () => registros.Count(registro => registro.Contains("CONEXION CERRADA:", StringComparison.Ordinal)) >= 2,
                TIEMPO_REGISTRO_LOG_MS);

            string[] log = registros.ToArray();

            verificar(
                log.Count(registro => registro.Contains("CONEXION ABIERTA:", StringComparison.Ordinal)) >= 2,
                "3.c servidor registra conexiones en consola",
                "Dos CONEXION ABIERTA",
                "Conexiones=" + log.Count(registro => registro.Contains("CONEXION ABIERTA:")));

            verificar(
                log.Any(registro => registro.Contains("COMANDO RECIBIDO: LIST", StringComparison.Ordinal)) &&
                log.Any(registro => registro.Contains("COMANDO RECIBIDO: HISTORY|EST003|2", StringComparison.Ordinal)) &&
                log.Any(registro => registro.Contains("COMANDO RECIBIDO: HELLO|1.0", StringComparison.Ordinal)),
                "3.c servidor registra comandos recibidos",
                "LIST, HISTORY, HELLO",
                "Comandos en log");

            verificar(
                log.Count(registro => registro.Contains("CONEXION CERRADA:", StringComparison.Ordinal)) >= 2,
                "3.c servidor registra desconexiones",
                "Dos CONEXION CERRADA",
                "Desconexiones=" + log.Count(registro => registro.Contains("CONEXION CERRADA:")));
        }
        finally
        {
            cancelacion.Cancel();

            try
            {
                tarea.Wait(TIEMPO_ESPERA_MS);
            }
            catch (AggregateException)
            {
                // Ignora la excepcion si ya fue reportada por una verificacion anterior
            }

            verificar(
                tarea.IsCompleted && !tarea.IsFaulted,
                "3.c servidor detiene el listener correctamente",
                "Task completada",
                "Completed=" + tarea.IsCompleted);
        }

        // Verifica que el cliente maneje un fallo de conexion cuando el servidor de destino no responde
        int puertoSinServidor;
        var listenerTemporal = new TcpListener(IPAddress.Loopback, 0);

        try
        {
            listenerTemporal.Start();
            puertoSinServidor = ((IPEndPoint)listenerTemporal.LocalEndpoint).Port;
        }
        finally
        {
            listenerTemporal.Stop();
        }

        using (var entradas = new StringReader($"CONNECT 127.0.0.1 {puertoSinServidor}\nSTATUS\nEXIT\n"))
        using (var salidas = new StringWriter())
        {
            ClienteTcp.Iniciar(entradas, salidas);
            string texto = salidas.ToString();

            verificar(
                texto.Contains("No se pudo conectar", StringComparison.Ordinal) &&
                texto.Contains("Sin conexion", StringComparison.Ordinal) &&
                texto.Contains("Cliente finalizado", StringComparison.Ordinal),
                "3.b error de conexion permite nuevo intento",
                "Fallo comprensible, consola viva",
                "Fallo controlado");
        }
    }
}
