using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Registra los resultados de las pruebas ejecutadas en el archivo Documentacion/Pruebas.md
internal sealed class RegistroPruebas : IDisposable
{
    private readonly List<string> filas = new List<string>();
    private readonly DateTimeOffset fecha = DateTimeOffset.Now;
    private int aprobadas;
    private int fallidas;
    private bool finalizado;

    // Agrega una fila a la tabla de resultados con el estado aprobado o fallido
    public void Agregar(string caso, bool aprobado, string esperado, string obtenido)
    {
        string estado = aprobado ? "APROBADO" : "FALLIDO";
        filas.Add($"| {Escapar(caso)} | {Escapar(esperado)} | {Escapar(obtenido)} | {estado} |");

        if (aprobado)
        {
            aprobadas++;
        }
        else
        {
            fallidas++;
        }
    }

    // Escribe el resumen de la ejecucion en el archivo Pruebas.md al finalizar
    public void Dispose()
    {
        if (finalizado)
        {
            return;
        }

        finalizado = true;

        try
        {
            string archivo = BuscarArchivoPruebas();
            string contenido = CrearBloque();
            File.AppendAllText(archivo, contenido, new UTF8Encoding(false));
            Console.WriteLine("Pruebas registradas en: " + archivo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("No se pudieron guardar las pruebas en Pruebas.md: " + ex.Message);
            Environment.ExitCode = 1;
        }
    }

    // Construye el bloque en formato Markdown con la tabla de comprobaciones y el resumen
    private string CrearBloque()
    {
        var constructorTexto = new StringBuilder();
        constructorTexto.AppendLine();
        constructorTexto.AppendLine($"### Ejecución: {fecha:yyyy-MM-dd HH:mm:ss zzz}");
        constructorTexto.AppendLine();
        constructorTexto.AppendLine("Origen: proyecto `Pruebas` (.NET 8, ejecución automática).");
        constructorTexto.AppendLine();
        constructorTexto.AppendLine("| Caso ejecutado | Resultado esperado | Resultado obtenido | Estado |");
        constructorTexto.AppendLine("|---|---|---|---|");

        foreach (string fila in filas)
        {
            constructorTexto.AppendLine(fila);
        }

        constructorTexto.AppendLine();
        constructorTexto.AppendLine(
            $"**Resumen:** {filas.Count} comprobaciones ejecutadas; " +
            $"{aprobadas} aprobadas; {fallidas} fallidas.");
        constructorTexto.AppendLine();

        return constructorTexto.ToString();
    }

    // Localiza la ruta del archivo Documentacion/Pruebas.md dentro de la solucion MeteoAula
    private static string BuscarArchivoPruebas()
    {
        for (DirectoryInfo? directorioActual = new DirectoryInfo(AppContext.BaseDirectory);
             directorioActual != null;
             directorioActual = directorioActual.Parent)
        {
            bool esSolucion =
                File.Exists(Path.Combine(directorioActual.FullName, "MeteoAula.sln")) ||
                File.Exists(Path.Combine(directorioActual.FullName, "MeteoAula.slnx"));

            string documentacion = Path.Combine(directorioActual.FullName, "Documentacion");

            if (esSolucion &&
                Directory.Exists(documentacion) &&
                Directory.Exists(Path.Combine(directorioActual.FullName, "Pruebas")))
            {
                return Path.Combine(documentacion, "Pruebas.md");
            }
        }

        throw new DirectoryNotFoundException(
            "No se encontró la carpeta Documentacion de la solución MeteoAula.");
    }

    // Escapa caracteres especiales para evitar que rompan la estructura de la tabla Markdown
    private static string Escapar(string texto)
    {
        string valor = texto ?? string.Empty;

        return valor.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    }
}
