using System;
using System.Windows;
using AutoFixScout.UI;

namespace AutoFixScout.UI;

/// <summary>
/// Portable launcher for v1 (no .NET SDK required at runtime).
/// Once packaged as self-contained EXE, this replaces dotnet run.
/// </summary>
class Program
{
    [STAThread]
    static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // Initialize app (orchestrator with read-only probes)
        var orchestrator = new Orchestrator();
        var mainWindow = new MainWindow(orchestrator);
        
        await mainWindow.ShowAsync();

        // Wait for close
        await System.Windows.Application.Current?.ShutdownAsync();
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.IsFatalException)
        {
            MessageBox.Show(
                "Unexpected error. Please report to your helper.\n" +
                $"Details: {e.ExceptionObject}\n\n" +
                "Session ID will be in the helper report.",
                "AutoFix Scout Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
