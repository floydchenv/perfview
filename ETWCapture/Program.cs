
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using System.Diagnostics;
public static class ETWCapture
{
    private static TextWriter Out = Console.Out;
    private static void ProcessEvent(TraceEvent data, SymbolReader symbolReader)
    {
        if (data == null)
        {
            return;
        }

        if ((Int32)data.Opcode == 46)
        {
            TraceCallStack callStack = data.CallStack();
            Out.WriteLine(callStack.ToString());
            if (callStack != null)
            {
                TraceCallStack cur = callStack;
                while (cur != null)
                {
                    Console.Out.WriteLine(cur.CodeAddress.ToString());
                    if (cur.CodeAddress != null && cur.CodeAddress.ModuleFile != null)
                    {
                        string ModuleName = cur.CodeAddress.ModuleFile.Name.ToLower();
                    }
            
                    cur = cur.Caller;
                }
            }
        }
    }

    public static void Run()
    {

        TraceEventSession session = null;
        using (session = new TraceEventSession("TraceLogSession"))
        {
            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.Profile,
                KernelTraceEventParser.Keywords.Profile
                );

            TextWriter SymbolLookupMessages = Out;        

            var symbolPath = new SymbolPath(SymbolPath.SymbolPathFromEnvironment).Add(SymbolPath.MicrosoftSymbolServerPath);
            SymbolReader symbolReader = new SymbolReader(SymbolLookupMessages, symbolPath.ToString());


            using (TraceLogEventSource traceLogSource = TraceLog.CreateFromTraceEventSession(session))
            {
                traceLogSource.Kernel.PerfInfoSample += ((SampledProfileTraceData data) => ProcessEvent(data, symbolReader));
                traceLogSource.Process();
            }
        }
    }
    private static void ResolveNativeCode(TraceCallStack callStack, SymbolReader symbolReader)
    {
        while (callStack != null)
        {
            var codeAddress = callStack.CodeAddress;
            if (codeAddress.Method == null)
            {
                var moduleFile = codeAddress.ModuleFile;
                if (moduleFile == null)
                {
                    Trace.WriteLine(string.Format("Could not find module for Address 0x{0:x}", codeAddress.Address));
                    continue;
                }
                else
                {
                    //codeAddress.CodeAddresses.LookupSymbolsForModule(symbolReader, moduleFile);
                }
                Out.WriteLine("{0}:0x{1:x}", moduleFile.Name, codeAddress.Address);
                Out.WriteLine("{0}::0x{1:x}", moduleFile.Name, codeAddress.Address);
            }
            callStack = callStack.Caller;
        }
        Out.WriteLine("--------------------------");

    }

}
public class Program
{
    public static void Main(string[] args)
    {
        ETWCapture.Run();
    }
}
