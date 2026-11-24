using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ClienteTCP.Red;

const string IP_POR_DEFECTO = "127.0.0.1";
const int PUERTO_POR_DEFECTO = 5050;
const int PUERTO_MINIMO = 1;
const int PUERTO_MAXIMO = 65535;

string ip = IP_POR_DEFECTO;
int puerto = PUERTO_POR_DEFECTO;

// Valida los argumentos opcionales de IP y puerto al iniciar la aplicacion
if (args.Length != 0)
{
    bool argumentosValidos =
        args.Length == 2 &&
        IPAddress.TryParse(args[0], out IPAddress? direccion) &&
        direccion.AddressFamily == AddressFamily.InterNetwork &&
        int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out puerto) &&
        puerto >= PUERTO_MINIMO &&
        puerto <= PUERTO_MAXIMO;

    if (!argumentosValidos)
    {
        Console.Error.WriteLine("Uso: ClienteTCP [IPV4 PUERTO] (puerto de 1 a 65535)");
        Environment.ExitCode = 1;
        return;
    }

    ip = args[0];
}

// Inicia la consola interactiva del cliente con el destino configurado
ClienteTcp.Iniciar(ip, puerto);
