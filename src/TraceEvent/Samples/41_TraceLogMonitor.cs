using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Timers;
using System.Collections;

namespace TraceEventSamples
{
    public struct FrameInfo
    {
        public UInt64 FrameId;
        public UInt64 TimestampBegin;
        public UInt64 TimestampEnd;
    }

    //每个模块的PDB信息
    public struct ModuleInfo
    {
        public UInt64 BaseAddress { get; set; }
        public Guid PDBGuid { get; set; }
        public ushort Age { get; set; }
        public string PDBName { get; set; }
    }

    //单个Event存在的各种数据和堆栈模块地址
    public class EventStackInfo
    {
        public UInt64 Timestamp { get; set; }
        public float Period { get; set; }
        public ushort ThreadId { get; set; }
        public ushort CPUId { get; set; }
        public List<ushort> ModuleIndexs = new List<ushort>(); 
        public List<UInt64> Addresses = new List<UInt64>();
    }
    
    //单个Event和FrameId的对照关系
    public struct EventInfo
    {
        public int EventId;
        public int FrameId;
        public UInt64 Timestamp;
    }

    //每个线程的信息
    public struct ThreadInfo
    {
        public ushort ThreadId;
        public string ThreadName;
    }

    //用于存储SampledProfileTraceData的RingBuffer
    public class RingBuffer
    {
        public EventStackInfo[] Buffer;
        public Int32 CurrentIndex;
        public RingBuffer(int size)
        {
            Buffer = new EventStackInfo[size];
            CurrentIndex = 0;
            for (int i = 0; i < size; i++)
            {
                Buffer[i] = new EventStackInfo();
            }
        }

        public EventStackInfo PopEventStack()
        {
            CurrentIndex = (CurrentIndex + 1) % Buffer.Length;
            return Buffer[CurrentIndex];
        }

        // 获取指定 FrameId 前后的事件
        public List<EventStackInfo> GetEventsInRange(ulong startTime, ulong endTime)
        {
            List<EventStackInfo> eventsInRange = new List<EventStackInfo>();
            for (int i = 0; i < Buffer.Length; i++)
            {
                // 如果事件在指定的时间范围内，则添加到结果列表中
                if ((UInt64)Buffer[i].Timestamp >= startTime && (UInt64)Buffer[i].Timestamp <= endTime)
                {
                    eventsInRange.Add(Buffer[i]);
                }
            }
            return eventsInRange;
        }
    }


    /// <summary>
    /// This is an example of using the Real-Time (non-file) based support of TraceLog to get stack traces for events.   
    /// </summary>
    internal class TraceLogMonitor
    {
        private static TextWriter Out = AllSamples.Out;

        // 创建一个 Stopwatch 实例
        private static Stopwatch stopwatch = new Stopwatch();
        private static List<long> AllTick = new List<long>();


        private static TraceEventSession session = null;
        private static TraceLogEventSource traceLogSource = null;

        private static TextWriter SymbolLookupMessages = Console.Out;
        private static SymbolPath symbolPath = new SymbolPath(SymbolPath.SymbolPathFromEnvironment).Add(SymbolPath.MicrosoftSymbolServerPath);
        private static SymbolReader symbolReader = new SymbolReader(SymbolLookupMessages, symbolPath.ToString());

        private static Int32 CallCount = 0;
        //存储所以模块信息的PDB内容
        private static Dictionary<string, TraceModuleFile> ModuleMap = new Dictionary<string, TraceModuleFile>();
        private static Dictionary<ushort, string> ModuleIndexMap = new Dictionary<ushort, string>();
        private static Dictionary<string, ushort> ModuleNameMap = new Dictionary<string, ushort>();


        //存储Event数据，循环录制2000ms内
        private static RingBuffer EventStackBuffers = new RingBuffer(2000);

        private static Dictionary<Int32, EventStackInfo> AllJankStackInfos = new Dictionary<Int32, EventStackInfo>();

        public static void SerializeToBinaryFile(List<KeyValuePair<Guid, UInt64>> list, string filePath)
        {
            using (var fileStream = new FileStream(filePath, FileMode.Create))
            using (var binaryWriter = new BinaryWriter(fileStream))
            {
                // 写入列表的长度
                binaryWriter.Write(list.Count);

                // 逐个序列化KeyValuePair
                foreach (var pair in list)
                {
                    // 写入Guid
                    binaryWriter.Write(pair.Key.ToByteArray());

                    // 写入UInt64
                    binaryWriter.Write(pair.Value);
                }
            }

            // 输出文件的大小
            FileInfo fileInfo = new FileInfo(filePath);
            Console.WriteLine($"File size: {fileInfo.Length} bytes");
        }

        //获取指定TimestampRank的EventStack
        public static void ProcessEventData(TraceEvent data)
        {

            TraceCallStack callStack = data.CallStack();
            if (callStack != null)
            {
                Out.WriteLine(callStack.ToString());

                EventStackInfo stackInfo = EventStackBuffers.PopEventStack();

                stackInfo.Timestamp = (ulong)data.TimeStamp.Ticks;
                stackInfo.ThreadId = (ushort)data.ThreadID;
                stackInfo.CPUId = (ushort)data.ProcessorNumber;

                stackInfo.ModuleIndexs.Clear();
                stackInfo.Addresses.Clear();
                TraceCallStack cur = callStack;
                while (cur != null)
                {
                    //Guid ModuleGuid = cur.CodeAddress.ModuleFile.PdbSignature;
                    if (cur.CodeAddress != null && cur.CodeAddress.ModuleFile != null)
                    {
                        string ModuleName = cur.CodeAddress.ModuleFile.Name;
                        ushort index = 0;
                        stackInfo.ModuleIndexs.Add(index);
                        stackInfo.Addresses.Add((UInt64)cur.CodeAddress.Address);
                    }

                    cur = cur.Caller;
                }
            }
        }

        public static void Run()
        {
            // Set up Ctrl-C to stop both user mode and kernel mode sessions
            Console.CancelKeyPress += (object sender, ConsoleCancelEventArgs cancelArgs) =>
            {
                if (session != null)
                {
                    session.Dispose();
                }

                cancelArgs.Cancel = true;
                Out.WriteLine("Finished");
            };

            using (session = new TraceEventSession("TraceLogSession"))
            {
                session.StackCompression = true;

                Out.WriteLine("Enabling Image load, Process and Thread events.  These are needed to look up native method names.");
                session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.Profile           
                    //| KernelTraceEventParser.Keywords.ContextSwitch 
                    | KernelTraceEventParser.Keywords.Process 
                    | KernelTraceEventParser.Keywords.ImageLoad 
                    | KernelTraceEventParser.Keywords.Thread
                    ,
                    KernelTraceEventParser.Keywords.Profile 
                    //| KernelTraceEventParser.Keywords.ContextSwitch
                    | KernelTraceEventParser.Keywords.Process
                    | KernelTraceEventParser.Keywords.ImageLoad
                    | KernelTraceEventParser.Keywords.Thread
                );

                symbolReader.SecurityCheck = (path => false);

                using (traceLogSource = TraceLog.CreateFromTraceEventSession(session))
                {

                    traceLogSource.Kernel.PerfInfoSample += ((SampledProfileTraceData data) => ProcessEvent(data, symbolReader));
                    traceLogSource.Process();
                }
            }

        }

        public static void StartCapture()
        {
            traceLogSource.Kernel.PerfInfoSample += ((SampledProfileTraceData data) => ProcessEvent(data, symbolReader));

        }

        public static void StopCapture()
        {
            traceLogSource.Kernel.PerfInfoSample -= ((SampledProfileTraceData data) => ProcessEvent(data, symbolReader));
        }

        public static void ReleaseResource()
        {
            if (session != null)
            {
                session.Dispose();
            }

            session = null;
        }

        public static void SetSampleProfile(float CpuSampleIntervalMSec)
        {
            if (session != null)
            {
                session.CpuSampleIntervalMSec = CpuSampleIntervalMSec;
            }
        }

        public static void DetectedJank(UInt64 FrameId)
        {

        }

        private static void ProcessEvent(TraceEvent data, SymbolReader symbolReader)
        {
            //46 : "Sample"
            if ((Int32)data.Opcode != 46)
            {
                return;
            }

            if (!data.ProcessName.Contains("DFMClient"))
            {
                //return;
            }


            stopwatch.Reset();
            stopwatch.Start();

            CallCount++;

            //Out.WriteLine("{0}: {1} ticks", (Int32)data.EventIndex, stopwatch.ElapsedTicks);
            ProcessEventData(data);

            //AllTick.Add(stopwatch.ElapsedTicks);
            stopwatch.Stop();
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
                    }
                    else
                    {
                        //codeAddress.CodeAddresses.LookupSymbolsForModule(symbolReader, moduleFile);
                    }
                }
                callStack = callStack.Caller;
            }
        }

        // Force it not to be inlined so we see the stack. 
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ThrowException()
        {
            ThrowException1();
        }

        // Force it not to be inlined so we see the stack. 
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ThrowException1()
        {
            Out.WriteLine("Causing an exception to happen so a CLR Exception Start event will be generated.");
            try
            {
                throw new Exception("This is a test exception thrown to generate a CLR event");
            }
            catch (Exception) { }
        }
    }
}
