
/// <summary>
/// Calls back at once, on the thread reporting. Progress&lt;T&gt; posts each report to the thread pool when there is no UI
/// thread: two can run at once, and a List added to from them broke and crashed the whole test run (seen 08-11).
/// Avisa enseguida, en el hilo que informa. Progress&lt;T&gt; manda cada aviso al grupo de hilos cuando no hay hilo de interfaz:
/// pueden correr dos a la vez, y una List a la que añadían se rompió y tumbó toda la pasada de pruebas (visto el 08-11).
/// </summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
