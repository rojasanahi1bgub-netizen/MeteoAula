using System;
using System.IO;
using System.Text;

namespace ServidorTCP.Protocolo
{
    // Define las constantes y la codificacion del protocolo METEO/1.0
    public static class EspecificacionMeteo
    {
        public const string NOMBRE_PROTOCOLO = "METEO/1.0";
        public const string VERSION_PROTOCOLO = "1.0";
        public const int MAX_BYTES_POR_LINEA = 512;

        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    }

    // Representa un error de validacion o estado del protocolo con su codigo asociado
    public sealed class ExcepcionProtocolo : Exception
    {
        public int Codigo { get; }

        // Inicializa la excepcion con el codigo y el mensaje de error del protocolo
        public ExcepcionProtocolo(int codigo, string mensaje) : base(mensaje)
        {
            Codigo = codigo;
        }
    }

    // Gestiona el encuadre, la validacion, la lectura y la escritura de tramas UTF-8 terminadas en LF
    public static class TramaProtocolo
    {
        // Construye una linea del protocolo uniendo los campos con el separador '|'
        public static string CrearLinea(params string[] campos)
        {
            if (campos == null || campos.Length == 0)
            {
                throw new ExcepcionProtocolo(402, "FORMATO_INVALIDO");
            }

            foreach (string campo in campos)
            {
                if (campo == null)
                {
                    throw new ExcepcionProtocolo(402, "CAMPO_NULO");
                }

                foreach (char caracter in campo)
                {
                    if (caracter == '|' || char.IsControl(caracter) || caracter == '\uFEFF')
                    {
                        throw new ExcepcionProtocolo(402, "CARACTER_NO_PERMITIDO");
                    }
                }
            }

            string linea = string.Join("|", campos);
            ValidarLinea(linea);

            return linea;
        }

        // Verifica que la linea no contenga caracteres de control ni supere los 512 bytes
        public static void ValidarLinea(string linea)
        {
            if (linea == null)
            {
                throw new ExcepcionProtocolo(402, "FORMATO_INVALIDO");
            }

            foreach (char caracter in linea)
            {
                if (char.IsControl(caracter) || caracter == '\uFEFF')
                {
                    throw new ExcepcionProtocolo(402, "CARACTER_NO_PERMITIDO");
                }
            }

            int cantidadBytes;

            try
            {
                cantidadBytes = EspecificacionMeteo.Utf8.GetByteCount(linea) + 1;
            }
            catch (EncoderFallbackException)
            {
                throw new ExcepcionProtocolo(402, "UTF8_INVALIDO");
            }

            if (cantidadBytes > EspecificacionMeteo.MAX_BYTES_POR_LINEA)
            {
                throw new ExcepcionProtocolo(413, "MENSAJE_DEMASIADO_GRANDE");
            }
        }

        // Codifica la linea en UTF-8 con delimitador LF y la envia por el flujo de red
        public static void Escribir(Stream flujo, string linea)
        {
            ValidarLinea(linea);
            byte[] datos = EspecificacionMeteo.Utf8.GetBytes(linea + "\n");
            flujo.Write(datos, 0, datos.Length);
            flujo.Flush();
        }

        // Lee bytes del flujo de red hasta encontrar el delimitador LF y valida la trama recibida
        public static string? Leer(Stream flujo)
        {
            using var buffer = new MemoryStream();

            while (true)
            {
                int siguienteByte = flujo.ReadByte();

                if (siguienteByte < 0)
                {
                    if (buffer.Length == 0)
                    {
                        return null;
                    }

                    throw new ExcepcionProtocolo(402, "MENSAJE_SIN_LF");
                }

                if (buffer.Length + 1 > EspecificacionMeteo.MAX_BYTES_POR_LINEA)
                {
                    throw new ExcepcionProtocolo(413, "MENSAJE_DEMASIADO_GRANDE");
                }

                if (siguienteByte == '\n')
                {
                    string linea;

                    try
                    {
                        linea = EspecificacionMeteo.Utf8.GetString(buffer.ToArray());
                    }
                    catch (DecoderFallbackException)
                    {
                        throw new ExcepcionProtocolo(402, "UTF8_INVALIDO");
                    }

                    ValidarLinea(linea);

                    return linea;
                }

                buffer.WriteByte((byte)siguienteByte);
            }
        }
    }
}
