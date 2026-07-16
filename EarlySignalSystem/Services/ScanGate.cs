namespace EarlySignalSystem.Services;

// Пази дали "Scan Now" веригата вече върви, за да не позволи /api/scan-now да енкюва втора паралелна
// верига. Semaphore-ът се държи през целия Hangfire chain (не само през enqueue-а в endpoint-а),
// затова release-ът е окачен като последна стъпка на веригата — виж Program.cs.
public static class ScanGate
{
    public static readonly SemaphoreSlim Semaphore = new(1, 1);

    public static void Release()
    {
        if (Semaphore.CurrentCount == 0)
        {
            Semaphore.Release();
        }
    }
}
