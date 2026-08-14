Imports System.Globalization
Imports System.IO
Imports System.Management
Imports Microsoft.Win32

' Namespace is "Probing", not "Diagnostics": VB SDK projects auto-import
' System.Diagnostics, and a same-named local namespace shadows it for every
' unqualified type in the assembly (Stopwatch, Process, Debug).
Namespace Probing

    Public Enum HostKind
        Unknown = 0
        PhysicalController
        VirtualMachine
        OfficeLite
    End Enum

    ''' <summary>
    ''' Describes the machine the probe is running on, and — more importantly —
    ''' says which measurements taken here may be trusted on a real controller.
    '''
    ''' WHY THIS MATTERS
    ''' ----------------
    ''' Phase 1 runs on KUKA.OfficeLite / VirtualKRC. That is the right place to
    ''' start: it costs nothing to break, and the CrossComm API surface is the
    ''' same one the physical controller exposes. But a virtualised KSS is not a
    ''' real-time system, and two classes of result do not transfer:
    '''
    '''   TRANSFERS  API shape — which services exist, method signatures, IIDs,
    '''              whether ShowMultiVar is present, the SetInfo subscription
    '''              limit, value formats returned by ShowVar, whether STA
    '''              callbacks arrive at all.
    '''
    '''   DOES NOT   Timing and load — callback jitter, minimum usable interval,
    '''   TRANSFER   CPU and memory cost, behaviour under production cycle
    '''              times, contention with smartHMI, and anything measured
    '''              while the robot is actually moving.
    '''
    ''' Concretely: a benchmark showing 0.3% CPU at a 200 ms interval on
    ''' OfficeLite says nothing about a KR C4 running a body-shop cycle. The
    ''' resource budgets in docs/01-architecture.md must be re-measured on
    ''' physical hardware before phase 5.
    '''
    ''' There is also a data caveat. On OfficeLite some variables are absent or
    ''' carry placeholder values — $KR_SERIALNO in particular may be empty or a
    ''' default, and it is the intended robot identity for mTLS certificates
    ''' (docs/06-protocol.md). Confirm it on physical hardware before building
    ''' the PKI around it.
    ''' </summary>
    Public Class HostDescription
        Public Property Kind As HostKind
        Public Property OsVersion As String
        Public Property MachineName As String
        Public Property ProcessorCount As Integer
        Public Property TotalMemoryMb As Long
        Public Property Is64BitOs As Boolean
        Public Property Is64BitProcess As Boolean
        Public Property DotNetRelease As Integer
        Public Property DotNetVersionName As String
        Public Property CrossCommRegistered As Boolean
        Public Property CrossCommPath As String
        Public Property KssPathPresent As Boolean
        Public Property Evidence As New List(Of String)

        Public ReadOnly Property TimingResultsAreRepresentative As Boolean
            Get
                Return Kind = HostKind.PhysicalController
            End Get
        End Property
    End Class

    Public NotInheritable Class EnvironmentInfo

        Private Sub New()
        End Sub

        ' Release values per Microsoft's documented .NET Framework mapping.
        Private Shared ReadOnly DotNetReleases As (Release As Integer, Name As String)() = {
            (533320, "4.8.1"), (528040, "4.8"), (461808, "4.7.2"),
            (461308, "4.7.1"), (460798, "4.7"), (394802, "4.6.2"),
            (394254, "4.6.1"), (393295, "4.6"), (379893, "4.5.2")
        }

        Public Shared Function Describe() As HostDescription
            Dim d As New HostDescription With {
                .OsVersion = Environment.OSVersion.VersionString,
                .MachineName = Environment.MachineName,
                .ProcessorCount = Environment.ProcessorCount,
                .Is64BitOs = Environment.Is64BitOperatingSystem,
                .Is64BitProcess = Environment.Is64BitProcess
            }

            DetectDotNet(d)
            DetectCrossComm(d)
            DetectKss(d)
            DetectMemory(d)
            DetectHostKind(d)

            Return d
        End Function

        Private Shared Sub DetectDotNet(d As HostDescription)
            Try
                Using key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).
                            OpenSubKey("SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full")
                    If key Is Nothing Then
                        d.DotNetVersionName = "(v4 Full not found)"
                        Return
                    End If

                    ' TryCast requires a reference type, so a boxed Integer is
                    ' tested with TypeOf and unboxed with DirectCast.
                    Dim rawRelease = key.GetValue("Release")
                    If TypeOf rawRelease Is Integer Then
                        Dim release = DirectCast(rawRelease, Integer)
                        d.DotNetRelease = release
                        For Each entry In DotNetReleases
                            If release >= entry.Release Then
                                d.DotNetVersionName = entry.Name
                                Return
                            End If
                        Next
                        d.DotNetVersionName = $"(release {release}, older than 4.5.2)"
                    Else
                        d.DotNetVersionName = "(Release value missing)"
                    End If
                End Using
            Catch ex As Exception When TypeOf ex Is Security.SecurityException OrElse
                                       TypeOf ex Is UnauthorizedAccessException
                d.DotNetVersionName = "(registry access denied)"
            End Try
        End Sub

        Private Shared Sub DetectCrossComm(d As HostDescription)
            Dim libId = Interop.NativeMethods.CrossKrcTypeLibId
            Dim subKey = $"TypeLib\{{{libId.ToString().ToUpperInvariant()}}}"

            Try
                Using root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry32).
                             OpenSubKey(subKey)
                    If root Is Nothing Then
                        d.CrossCommRegistered = False
                        d.Evidence.Add($"HKCR\{subKey} absent in 32-bit view")
                        Return
                    End If

                    d.CrossCommRegistered = True

                    For Each versionName In root.GetSubKeyNames()
                        Using ver = root.OpenSubKey($"{versionName}\0\win32")
                            Dim path = TryCast(ver?.GetValue(""), String)
                            If Not String.IsNullOrEmpty(path) Then
                                d.CrossCommPath = path
                                d.Evidence.Add($"TypeLib {versionName} -> {path}")
                                Exit For
                            End If
                        End Using
                    Next
                End Using
            Catch ex As Exception When TypeOf ex Is Security.SecurityException OrElse
                                       TypeOf ex Is UnauthorizedAccessException
                d.Evidence.Add("TypeLib registry read denied")
            End Try
        End Sub

        Private Shared Sub DetectKss(d As HostDescription)
            Dim candidates = {"C:\KRC", "C:\KRC\ROBOTER", "D:\KRC"}
            For Each path In candidates
                Try
                    If Directory.Exists(path) Then
                        d.KssPathPresent = True
                        d.Evidence.Add($"KSS directory present: {path}")
                        Return
                    End If
                Catch ex As IOException
                Catch ex As UnauthorizedAccessException
                End Try
            Next
        End Sub

        Private Shared Sub DetectMemory(d As HostDescription)
            Try
                Using searcher As New ManagementObjectSearcher(
                    "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem")
                    For Each mo As ManagementObject In searcher.Get()
                        Dim raw = mo("TotalPhysicalMemory")
                        If raw IsNot Nothing Then
                            Dim bytes = Convert.ToUInt64(raw, CultureInfo.InvariantCulture)
                            d.TotalMemoryMb = CLng(bytes \ (1024UL * 1024UL))
                        End If
                        Exit For
                    Next
                End Using
            Catch ex As ManagementException
                d.Evidence.Add("WMI memory query failed: " & ex.Message)
            End Try
        End Sub

        ''' <summary>
        ''' Distinguishes physical controller from VM. Detection is heuristic —
        ''' when in doubt it reports VirtualMachine, because treating a VM's
        ''' timing numbers as representative is the expensive mistake, and
        ''' treating a physical controller's numbers as unrepresentative is
        ''' merely conservative.
        ''' </summary>
        Private Shared Sub DetectHostKind(d As HostDescription)
            Dim manufacturer As String = Nothing
            Dim model As String = Nothing

            Try
                Using searcher As New ManagementObjectSearcher(
                    "SELECT Manufacturer, Model FROM Win32_ComputerSystem")
                    For Each mo As ManagementObject In searcher.Get()
                        manufacturer = Convert.ToString(mo("Manufacturer"))
                        model = Convert.ToString(mo("Model"))
                        Exit For
                    Next
                End Using
            Catch ex As ManagementException
                d.Evidence.Add("WMI system query failed: " & ex.Message)
            End Try

            If manufacturer IsNot Nothing OrElse model IsNot Nothing Then
                d.Evidence.Add($"Win32_ComputerSystem: {manufacturer} / {model}")
            End If

            Dim haystack = $"{manufacturer} {model}".ToUpperInvariant()
            Dim vmMarkers = {"VMWARE", "VIRTUALBOX", "INNOTEK", "QEMU", "KVM",
                             "XEN", "PARALLELS", "BOCHS", "HYPER-V", "VIRTUAL MACHINE",
                             "MICROSOFT CORPORATION VIRTUAL"}

            Dim looksVirtual = vmMarkers.Any(Function(m) haystack.Contains(m))

            ' OfficeLite ships a virtual KRC with no drive hardware. Its
            ' presence is a stronger signal than the hypervisor markers.
            Dim officeLiteMarkers = {"C:\KRC\OfficeLite", "C:\Program Files\KUKA\OfficeLite",
                                     "C:\KRC\ROBOTER\INIT\OfficeLite.ini"}
            For Each marker In officeLiteMarkers
                Try
                    If Directory.Exists(marker) OrElse File.Exists(marker) Then
                        d.Kind = HostKind.OfficeLite
                        d.Evidence.Add($"OfficeLite marker: {marker}")
                        Return
                    End If
                Catch ex As IOException
                Catch ex As UnauthorizedAccessException
                End Try
            Next

            If looksVirtual Then
                d.Kind = HostKind.VirtualMachine
            ElseIf d.KssPathPresent Then
                d.Kind = HostKind.PhysicalController
            Else
                d.Kind = HostKind.Unknown
            End If
        End Sub

        ''' <summary>
        ''' Warnings that must be printed alongside any measurement taken on
        ''' this host, so a number from OfficeLite is never quoted later as if
        ''' it came from a controller.
        ''' </summary>
        Public Shared Function TimingCaveats(d As HostDescription) As List(Of String)
            Dim notes As New List(Of String)

            Select Case d.Kind
                Case HostKind.PhysicalController
                    notes.Add("Physical controller detected: timing and load figures are representative.")
                    notes.Add("Measure again while the robot is running a production cycle, not idle.")

                Case HostKind.OfficeLite, HostKind.VirtualMachine, HostKind.Unknown
                    notes.Add("NOT a physical controller: timing and load figures are NOT representative.")
                    notes.Add("Transfers to real hardware:     API shape, signatures, IIDs, SetInfo limit,")
                    notes.Add("                                ShowVar value formats, STA callback delivery.")
                    notes.Add("Does NOT transfer:              CPU/RAM cost, callback jitter, minimum usable")
                    notes.Add("                                interval, contention with smartHMI.")
                    notes.Add("$KR_SERIALNO may be empty or a placeholder here. Confirm on hardware before")
                    notes.Add("using it as the certificate identity (docs/06-protocol.md).")
            End Select

            If d.Is64BitProcess Then
                notes.Add("WARNING: running as a 64-bit process. WBC_KrcLib is 32-bit and will not load. " &
                          "Rebuild with PlatformTarget=x86.")
            End If

            Return notes
        End Function

    End Class

End Namespace
