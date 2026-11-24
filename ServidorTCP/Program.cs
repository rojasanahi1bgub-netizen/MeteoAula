using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ServidorTCP.Red;

const int PUERTO_POR_DEFECTO = 5050;
const int PUERTO_MINIMO = 1;
const int PUERTO_MAXIMO = 65535;

int puerto = PUERTO_POR_DEFECTO;
IPAddress direccionEscucha = IPAddress.Loopback;

// Valida los argumentos de linea de comandos para puerto e IPv4 de escucha
bool argumentosInvalidos =
    args.Length > 2 ||
    (args.Length > 0 && (!int.TryParse(args[0], out puerto) || puerto < PUERTO_MINIMO || puerto > PUERTO_MAXIMO)) ||
    (args.Length == 2 && (!IPAddress.TryParse(args[1], out IPAddress? ipParseada) ||
                          ipParseada.AddressFamily != AddressFamily.InterNetwork));

if (argumentosInvalidos)
{
    Console.Error.WriteLine("Uso: ServidorTCP [PUERTO [IP_ESCUCHA_IPV4]]");
    Console.Error.WriteLine("Ejemplo LAN: ServidorTCP 5050 0.0.0.0");
    Environment.ExitCode = 1;
    return;
}

if (args.Length == 2)
{
    direccionEscucha = IPAddress.Parse(args[1]);
}

// Configura la cancelacion con Ctrl+C para detener el servidor y liberar el puerto
using var cancelacion = new CancellationTokenSource();
ConsoleCancelEventHandler alCerrar = (_, evento) =>
{
    evento.Cancel = true;
    cancelacion.Cancel();
};
Console.CancelKeyPress += alCerrar;

try
{
    Console.WriteLine("Ctrl+C detiene el servidor de forma ordenada.");
    ServidorTcp servidor = new ServidorTcp();
    servidor.Iniciar(puerto, cancelacion.Token, direccionEscucha);
}
catch (Exception ex)
{
    Console.Error.WriteLine("No fue posible iniciar o mantener el servidor: " + ex.Message);
    Environment.ExitCode = 1;
}
finally
{
    Console.CancelKeyPress -= alCerrar;
}
