using System.Runtime.InteropServices;

// Rodando direto pelo .exe: um clique na janela do console (QuickEdit) congela a escrita do log e,
// com ela, o programa, até alguém apertar Enter/Esc. Desliga isso.
static partial class ConsoleMode
{
    const int StdInput = -10;
    const uint EnableQuickEdit = 0x0040, EnableExtendedFlags = 0x0080;

    [LibraryImport("kernel32.dll")] private static partial IntPtr GetStdHandle(int handle);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetConsoleMode(IntPtr h, out uint mode);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetConsoleMode(IntPtr h, uint mode);

    public static void DisableQuickEdit()
    {
        var h = GetStdHandle(StdInput);
        if (GetConsoleMode(h, out var mode))
            SetConsoleMode(h, (mode & ~EnableQuickEdit) | EnableExtendedFlags);
    }
}
