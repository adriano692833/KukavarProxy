Imports System.Globalization
Imports System.Runtime.InteropServices.ComTypes
Imports KrcMonitor.Probe.CrossComm
Imports KrcMonitor.Probe.Interop
Imports KrcMonitor.Probe.Krl
Imports KrcMonitor.Probe.Probing

''' <summary>
''' KrcMonitor.Probe — read-only diagnostic tool for the KUKA CrossComm
''' (WBC_KrcLib) COM API.
'''
''' This is phase 1 tooling. It answers the open API questions listed in
''' docs/02-crosscomm-api.md section 12 so the production agent can be built
''' against facts rather than inference from the KukavarProxy source.
'''
''' SAFETY
''' ------
''' The probe never writes a variable, never selects, starts, stops or cancels
''' a program, and never resets an I/O bus. ServiceIds.MutatingMembers is the
''' enforcement point. Obtaining a service interface pointer is inert and is
''' used to report availability; invoking anything on SyncSelect or SyncIo is
''' not implemented and must not be added.
''' </summary>
Module Program

    Private Const DefaultClientName As String = "KRCPROBE"
    Private Const ExitOk As Integer = 0
    Private Const ExitUsage As Integer = 2
    Private Const ExitUnavailable As Integer = 3
    Private Const ExitFailed As Integer = 4

    Function Main(args As String()) As Integer
        Console.OutputEncoding = Text.Encoding.UTF8

        Dim cli As CommandLine
        Try
            cli = CommandLine.Parse(args)
        Catch ex As ArgumentException
            Console.Error.WriteLine("error: " & ex.Message)
            Console.Error.WriteLine()
            PrintUsage()
            Return ExitUsage
        End Try

        If cli.Command = "help" OrElse cli.Command Is Nothing Then
            PrintUsage()
            Return If(cli.Command Is Nothing, ExitUsage, ExitOk)
        End If

        Try
            Select Case cli.Command
                Case "env" : Return RunEnv()
                Case "dump-typelib" : Return RunDumpTypeLib(cli)
                Case "list-services" : Return RunListServices(cli)
                Case "read" : Return RunRead(cli)
                Case "read-multi" : Return RunReadMulti(cli)
                Case Else
                    Console.Error.WriteLine($"error: unknown command '{cli.Command}'")
                    PrintUsage()
                    Return ExitUsage
            End Select

        Catch ex As CrossCommUnavailableException
            Console.Error.WriteLine()
            Console.Error.WriteLine("CrossComm unavailable")
            Console.Error.WriteLine("  " & ex.Message)
            Return ExitUnavailable

        Catch ex As StaCallTimeoutException
            Console.Error.WriteLine()
            Console.Error.WriteLine("Call timed out")
            Console.Error.WriteLine("  " & ex.Message)
            Console.Error.WriteLine()
            Console.Error.WriteLine("  This is the R-03 scenario from docs/04-risk-and-safety.md.")
            Console.Error.WriteLine("  CrossComm can block indefinitely during Power-On, program restart,")
            Console.Error.WriteLine("  or while a $STOPMESS is active. The production agent must treat a")
            Console.Error.WriteLine("  timeout as a circuit-breaker trip, never as a reason to retry.")
            Return ExitFailed

        Catch ex As CrossCommCallException
            Console.Error.WriteLine()
            Console.Error.WriteLine("CrossComm call failed")
            Console.Error.WriteLine("  " & ex.Message)
            Return ExitFailed
        End Try
    End Function

    ' ---------------------------------------------------------------- env ---

    Private Function RunEnv() As Integer
        Dim d = EnvironmentInfo.Describe()

        Header("Host environment")
        Field("Machine", d.MachineName)
        Field("OS", d.OsVersion)
        Field("OS bitness", If(d.Is64BitOs, "64-bit", "32-bit"))
        Field("Process bitness", If(d.Is64BitProcess, "64-bit  <-- WRONG", "32-bit  (correct)"))
        Field("Processors", d.ProcessorCount.ToString(CultureInfo.InvariantCulture))
        Field("Physical memory", $"{d.TotalMemoryMb} MB")
        Field(".NET Framework", $"{d.DotNetVersionName} (release {d.DotNetRelease})")
        Field("Host kind", d.Kind.ToString())
        Field("KSS directory", If(d.KssPathPresent, "present", "not found"))
        Field("CrossComm TypeLib", If(d.CrossCommRegistered, "registered", "NOT registered"))
        If Not String.IsNullOrEmpty(d.CrossCommPath) Then
            Field("TypeLib path", d.CrossCommPath)
        End If

        If d.Evidence.Count > 0 Then
            Console.WriteLine()
            Header("Detection evidence")
            For Each e In d.Evidence
                Console.WriteLine("  " & e)
            Next
        End If

        Console.WriteLine()
        Header("Measurement validity")
        For Each note In EnvironmentInfo.TimingCaveats(d)
            Console.WriteLine("  " & note)
        Next

        Console.WriteLine()
        Return If(d.CrossCommRegistered, ExitOk, ExitUnavailable)
    End Function

    ' ------------------------------------------------------- dump-typelib ---

    Private Function RunDumpTypeLib(cli As CommandLine) As Integer
        Dim info = TypeLibInspector.Load(cli.GetOption("typelib"))

        Header($"Type library: {info.Name}")
        Field("Description", info.DocString)
        Field("LIBID", info.LibId.ToString("B").ToUpperInvariant())
        Field("Version", $"{info.MajorVersion}.{info.MinorVersion}")
        Field("Path", info.Path)
        Field("Types", info.Types.Count.ToString(CultureInfo.InvariantCulture))
        Console.WriteLine()

        Dim filter = cli.GetOption("filter")

        For Each t In info.Types
            If filter IsNot Nothing AndAlso
               t.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 Then
                Continue For
            End If

            Console.WriteLine($"{KindLabel(t.Kind)} {t.Name}")
            If Not String.IsNullOrEmpty(t.DocString) Then
                Console.WriteLine($"    ' {t.DocString}")
            End If
            If t.Iid <> Guid.Empty Then
                Console.WriteLine($"    IID  {t.Iid.ToString("B").ToUpperInvariant()}")
            End If
            For Each impl In t.ImplementedInterfaces
                Console.WriteLine($"    :    {impl}")
            Next

            For Each m In t.Members
                Console.WriteLine($"    [vtbl {m.VTableOffset,4}] {m.ToSignature()}")
            Next

            Console.WriteLine()
        Next

        Console.WriteLine("Notes for the agent implementation:")
        Console.WriteLine("  * The IIDs above are what a <ComImport, Guid(...)> declaration needs.")
        Console.WriteLine("  * Vtable order is declaration order — VB.NET interface members must be")
        Console.WriteLine("    declared in ascending vtbl offset, or calls dispatch to the wrong slot.")
        Console.WriteLine("  * Check ICKSyncVar for ShowMultiVar: its presence and parameter shape")
        Console.WriteLine("    decide whether batched reads are possible (docs/02-crosscomm-api.md 4).")
        Console.WriteLine()

        Return ExitOk
    End Function

    Private Function KindLabel(k As TYPEKIND) As String
        Select Case k
            Case TYPEKIND.TKIND_INTERFACE : Return "interface "
            Case TYPEKIND.TKIND_DISPATCH : Return "dispinterface"
            Case TYPEKIND.TKIND_COCLASS : Return "coclass   "
            Case TYPEKIND.TKIND_ENUM : Return "enum      "
            Case TYPEKIND.TKIND_RECORD : Return "struct    "
            Case TYPEKIND.TKIND_ALIAS : Return "alias     "
            Case TYPEKIND.TKIND_MODULE : Return "module    "
            Case TYPEKIND.TKIND_UNION : Return "union     "
            Case Else : Return "type      "
        End Select
    End Function

    ' ------------------------------------------------------ list-services ---

    Private Function RunListServices(cli As CommandLine) As Integer
        Using sta As New StaHost("KrcProbe.STA")
            Using session = NewSession(sta, cli)
                session.Connect()

                Header($"CrossComm services (client name '{session.ClientName}')")
                Console.WriteLine()

                Dim results = session.ProbeServices(includeNonAgentServices:=True)

                For Each r In results
                    Dim status = If(r.Available, "available", "UNAVAILABLE")
                    Dim scope = If(r.AllowedForAgent, "agent", "probe-only")
                    Console.WriteLine($"  {r.ProgId,-30} {status,-12} [{scope}]")
                    If Not String.IsNullOrEmpty(r.ErrorText) Then
                        Console.WriteLine($"      {r.ErrorText}")
                    End If
                Next

                Console.WriteLine()
                Console.WriteLine("  [agent]      obtained by the production agent")
                Console.WriteLine("  [probe-only] existence reported here; never imported into the agent")
                Console.WriteLine("               (SyncSelect and SyncIo control program flow and I/O —")
                Console.WriteLine("                see docs/04-risk-and-safety.md section 2)")
                Console.WriteLine()

                Return If(results.Any(Function(r) r.AllowedForAgent AndAlso r.Available),
                          ExitOk, ExitUnavailable)
            End Using
        End Using
    End Function

    ' --------------------------------------------------------------- read ---

    Private Function RunRead(cli As CommandLine) As Integer
        Dim names = cli.Positionals
        If names.Count = 0 Then
            Console.Error.WriteLine("error: 'read' requires at least one variable name")
            Return ExitUsage
        End If

        Using sta As New StaHost("KrcProbe.STA")
            Using session = NewSession(sta, cli)
                session.Connect()

                Header("Variable read (ICKSyncVar.ShowVar)")
                Console.WriteLine()

                Dim failures = 0
                For Each name In names
                    Dim sw = Stopwatch.StartNew()
                    Dim raw As String = Nothing
                    Dim failed = False

                    Try
                        raw = session.ShowVar(name)
                    Catch ex As CrossCommCallException
                        failed = True
                        failures += 1
                        Console.WriteLine($"  {name}")
                        Console.WriteLine($"      FAILED  {ex.Message}")
                    End Try
                    sw.Stop()

                    If failed Then Continue For

                    Console.WriteLine($"  {name}   ({sw.Elapsed.TotalMilliseconds:F1} ms)")

                    If raw Is Nothing Then
                        Console.WriteLine("      raw     (Nothing)")
                        Console.WriteLine("      note    ShowVar returned null. On a controller this usually")
                        Console.WriteLine("              means the variable does not exist in this context.")
                    ElseIf raw.Length = 0 Then
                        Console.WriteLine("      raw     (empty string)")
                    Else
                        Console.WriteLine($"      raw     {raw}")
                        Dim parsed = KrlValueParser.Parse(raw)
                        Console.WriteLine($"      kind    {parsed.Kind}")
                        If parsed.IsStructure Then
                            If Not String.IsNullOrEmpty(parsed.StructTypeName) Then
                                Console.WriteLine($"      struct  {parsed.StructTypeName}")
                            End If
                            For Each kv In parsed.Fields
                                Console.WriteLine($"        {kv.Key,-6} {kv.Value}")
                            Next
                        Else
                            Console.WriteLine($"      value   {parsed.Scalar}")
                        End If
                    End If

                    Console.WriteLine()
                Next

                Return If(failures = 0, ExitOk, ExitFailed)
            End Using
        End Using
    End Function

    ' --------------------------------------------------------- read-multi ---

    Private Function RunReadMulti(cli As CommandLine) As Integer
        Dim names = cli.Positionals
        If names.Count < 2 Then
            Console.Error.WriteLine("error: 'read-multi' requires at least two variable names")
            Return ExitUsage
        End If

        Using sta As New StaHost("KrcProbe.STA")
            Using session = NewSession(sta, cli)
                session.Connect()

                Header("Batched read probe (ICKSyncVar.ShowMultiVar)")
                Console.WriteLine()
                Console.WriteLine("  ShowMultiVar is never called by KukavarProxy. Its existence is implied")
                Console.WriteLine("  by ICKCallbackVar.OnShowMultiVar (cCrossComm.cls:1025) but its signature")
                Console.WriteLine("  is unknown, so each plausible marshalling is tried in turn.")
                Console.WriteLine()
                Console.WriteLine($"  Variables: {String.Join(", ", names)}")
                Console.WriteLine()

                Dim log As List(Of String) = Nothing
                Dim result = session.TryShowMultiVar(names.ToArray(), log)

                Header("Attempts")
                For Each line In log
                    Console.WriteLine("  " & line)
                Next
                Console.WriteLine()

                If result Is Nothing Then
                    Console.WriteLine("  RESULT: no batched read available.")
                    Console.WriteLine("  The agent must issue one ShowVar per variable, or rely on SetInfo")
                    Console.WriteLine("  subscriptions instead of polling. Record this in")
                    Console.WriteLine("  docs/02-crosscomm-api.md section 4.")
                    Console.WriteLine()
                    Return ExitFailed
                End If

                Console.WriteLine("  RESULT: batched read works. Update docs/02-crosscomm-api.md section 4")
                Console.WriteLine("  with the accepted signature — this removes N-1 COM round trips per sample.")
                Console.WriteLine()

                ' Baseline comparison, so the win is quantified rather than assumed.
                Dim swSingle = Stopwatch.StartNew()
                For Each n In names
                    Try
                        session.ShowVar(n)
                    Catch ex As CrossCommCallException
                    End Try
                Next
                swSingle.Stop()

                Console.WriteLine($"  {names.Count} sequential ShowVar calls: {swSingle.Elapsed.TotalMilliseconds:F1} ms")
                Console.WriteLine()
                Console.WriteLine("  Timing is only meaningful on physical hardware — run 'env' first.")
                Console.WriteLine()

                Return ExitOk
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------ helpers ---

    Private Function NewSession(sta As StaHost, cli As CommandLine) As CrossCommSession
        Dim clientName = If(cli.GetOption("client"), DefaultClientName)
        Dim timeoutMs = cli.GetOptionInt("timeout", 5000)
        Return New CrossCommSession(sta, clientName, TimeSpan.FromMilliseconds(timeoutMs))
    End Function

    Private Sub Header(text As String)
        Console.WriteLine(text)
        Console.WriteLine(New String("-"c, Math.Min(text.Length, 72)))
    End Sub

    Private Sub Field(name As String, value As String)
        Console.WriteLine($"  {name,-20} {If(value, "(unknown)")}")
    End Sub

    Private Sub PrintUsage()
        Console.WriteLine("KrcMonitor.Probe — read-only CrossComm diagnostics")
        Console.WriteLine()
        Console.WriteLine("USAGE")
        Console.WriteLine("  KrcMonitor.Probe.exe <command> [arguments] [options]")
        Console.WriteLine()
        Console.WriteLine("COMMANDS")
        Console.WriteLine("  env")
        Console.WriteLine("      Report host, .NET, CrossComm registration, and whether timing")
        Console.WriteLine("      measured here is representative of a physical controller.")
        Console.WriteLine("      Run this first on any new machine.")
        Console.WriteLine()
        Console.WriteLine("  dump-typelib [--typelib <path>] [--filter <text>]")
        Console.WriteLine("      Enumerate WBC_KrcLib interfaces, methods, IIDs and vtable offsets")
        Console.WriteLine("      straight from the type library. Reads metadata only; creates no")
        Console.WriteLine("      CrossComm object and cannot affect the controller.")
        Console.WriteLine()
        Console.WriteLine("  list-services")
        Console.WriteLine("      Report which WBC_KrcLib services this controller offers.")
        Console.WriteLine()
        Console.WriteLine("  read <var> [<var> ...]")
        Console.WriteLine("      Read variables via ShowVar and show raw plus parsed values.")
        Console.WriteLine()
        Console.WriteLine("  read-multi <var> <var> [...]")
        Console.WriteLine("      Probe for a batched read and compare against sequential reads.")
        Console.WriteLine()
        Console.WriteLine("OPTIONS")
        Console.WriteLine("  --client <name>    CrossComm client name (default: " & DefaultClientName & ")")
        Console.WriteLine("  --timeout <ms>     Per-call timeout (default: 5000)")
        Console.WriteLine("  --typelib <path>   Load Cross3Krc.CIE from an explicit path")
        Console.WriteLine("  --filter <text>    Restrict dump-typelib output")
        Console.WriteLine()
        Console.WriteLine("EXAMPLES")
        Console.WriteLine("  KrcMonitor.Probe.exe env")
        Console.WriteLine("  KrcMonitor.Probe.exe dump-typelib --filter SyncVar")
        Console.WriteLine("  KrcMonitor.Probe.exe read $KR_SERIALNO $MODEL_NAME[] $POS_ACT")
        Console.WriteLine("  KrcMonitor.Probe.exe read-multi $POS_ACT $AXIS_ACT $OV_PRO")
        Console.WriteLine()
        Console.WriteLine("SAFETY")
        Console.WriteLine("  Read-only. No variable is written, no program is selected, started,")
        Console.WriteLine("  stopped or cancelled, and no I/O bus is reset.")
        Console.WriteLine()
        Console.WriteLine("EXIT CODES")
        Console.WriteLine("  0 ok   2 usage   3 CrossComm unavailable   4 call failed")
    End Sub

End Module
