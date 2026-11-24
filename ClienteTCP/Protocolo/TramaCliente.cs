using System;
using System.IO;
using System.Text;

namespace ClienteTCP.Protocolo
{
    // Gestiona el envio y la lectura de tramas METEO/1.0 en UTF-8 terminadas en LF
    internal static class TramaCliente
    {
        private const int MAX_BYTES_POR_LINEA = 512;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        // Construye una trama uniendo los campos con '|' y la envia por el flujo TCP
        public static void Enviar(Stream flujo, params string[] campos)
        {
            if (campos.Length == 0)
            {
                throw new InvalidDataException("Mensaje vacio");
            }

            foreach (string campo in campos)
            {
                if (campo == null)
                {
                    throw new InvalidDataException("Campo nulo");
                }

                foreach (char caracter in campo)
                {
                    if (caracter == '|' || char.IsControl(caracter) || caracter == '\uFEFF')
                    {
                        throw new InvalidDataException("Campo con separador o control");
                    }
                }
            }

            byte[] bytes = Utf8.GetBytes(string.Join("|", campos) + "\n");

            if (bytes.Length > MAX_BYTES_POR_LINEA)
            {
                throw new InvalidDataException("El mensaje excede 512 bytes incluido LF");
            }

            flujo.Write(bytes, 0, bytes.Length);
            flujo.Flush();
        }

        // Lee una linea del flujo TCP hasta encontrar LF y separa los campos por '|'
        public static string[] Leer(Stream flujo)
        {
            using var buffer = new MemoryStream();

            while (true)
            {
                int dato = flujo.ReadByte();

                if (dato < 0)
                {
                    throw new EndOfStreamException("Conexion cerrada antes de completar la respuesta");
                }

                if (buffer.Length + 1 > MAX_BYTES_POR_LINEA)
                {
                    throw new InvalidDataException("Respuesta supera 512 bytes por linea");
                }

                if (dato == '\n')
                {
                    string texto;

                    try
                    {
                        texto = Utf8.GetString(buffer.ToArray());
                    }
                    catch (DecoderFallbackException ex)
                    {
                        throw new InvalidDataException("UTF-8 invalido", ex);
                    }

                    foreach (char caracter in texto)
                    {
                        if (char.IsControl(caracter) || caracter == '\uFEFF')
                        {
                            throw new InvalidDataException("Caracter de control o BOM no permitido");
                        }
                    }

                    return texto.Split('|');
                }

                buffer.WriteByte((byte)dato);
            }
        }
    }
}
