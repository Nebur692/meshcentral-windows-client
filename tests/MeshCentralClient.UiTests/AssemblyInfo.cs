using Xunit;

// UI tests share one desktop, keyboard and screen: they must run one after another.
// Las pruebas de interfaz comparten escritorio, teclado y pantalla: tienen que ir una detrás de otra.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
