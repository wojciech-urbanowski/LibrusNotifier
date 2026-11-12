namespace LibrusNotifier;

/// <summary>
/// Blokada jednej instancji: plik otwarty na wyłączność (Windows: brak współdzielenia, Linux: flock).
/// System zwalnia ją także po awarii procesu. Plik nie jest usuwany, żeby nie tworzyć wyścigów.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    /// <returns>Blokada albo <c>null</c>, gdy trzyma ją inny proces.</returns>
    public static InstanceLock? TryAcquire(string path)
    {
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            try
            {
                stream.SetLength(0);
                var pid = System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                stream.Write(pid);
                stream.Flush();
            }
            catch (IOException)
            {
                // Zapis PID jest tylko informacyjny.
            }
            return new InstanceLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
