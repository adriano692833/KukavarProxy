Imports System.Globalization
Imports System.Reflection
Imports System.Runtime.InteropServices
Imports KrcMonitor.Probe.Interop

Namespace CrossComm

    ''' <summary>
    ''' Service ProgIDs exposed by the KrcServiceFactory, as used by
    ''' cCrossComm.ConnectToCross (cCrossComm.cls:154-174).
    ''' </summary>
    Public NotInheritable Class ServiceIds

        Private Sub New()
        End Sub

        Public Const ServiceFactoryProgId As String = "WBC_KrcLib.KrcServiceFactory"

        Public Const SyncVar As String = "WBC_KrcLib.SyncVar"
        Public Const AsyncVar As String = "WBC_KrcLib.AsyncVar"
        Public Const AdviseMessage As String = "WBC_KrcLib.AdviseMessage"
        Public Const SyncFile As String = "WBC_KrcLib.SyncFile"
        Public Const SyncEdit As String = "WBC_KrcLib.SyncEdit"
        Public Const SyncSelect As String = "WBC_KrcLib.SyncSelect"
        Public Const SyncIo As String = "WBC_KrcLib.SyncIo"

        ''' <summary>
        ''' Services the production agent is permitted to obtain. SyncSelect and
        ''' SyncIo are absent by design — see docs/04-risk-and-safety.md.
        ''' </summary>
        Public Shared ReadOnly AgentAllowed As String() = {SyncVar, AsyncVar, AdviseMessage}

        ''' <summary>
        ''' Every known service. The probe may enumerate all of them to report
        ''' availability, but see <see cref="MutatingMembers"/>.
        ''' </summary>
        Public Shared ReadOnly All As String() = {
            SyncVar, AsyncVar, AdviseMessage, SyncFile, SyncEdit, SyncSelect, SyncIo
        }

        ''' <summary>
        ''' Members that change controller state. The probe refuses to invoke
        ''' any of these, whatever the caller asks for.
        '''
        ''' Obtaining an interface pointer is inert — it is a QueryInterface and
        ''' touches no robot state — so --list-services may legitimately probe
        ''' SyncSelect and SyncIo to report whether they exist. Calling a method
        ''' on them is an entirely different act, and this list is the guard.
        ''' </summary>
        Public Shared ReadOnly MutatingMembers As String() = {
            "SetVar", "SetMultiVar", "SetInfoOff",
            "Select", "Stop", "Cancel", "Confirm",
            "IoControl",
            "Copy", "CopyMem2File", "Delete", "Rename", "MkDir", "RmDir",
            "Save", "Write", "Insert", "Replace"
        }

        Public Shared Function IsMutating(memberName As String) As Boolean
            If String.IsNullOrEmpty(memberName) Then Return False
            For Each m In MutatingMembers
                If String.Equals(m, memberName, StringComparison.OrdinalIgnoreCase) Then
                    Return True
                End If
            Next
            Return False
        End Function

    End Class

    Public Class ServiceAvailability
        Public Property ProgId As String
        Public Property Available As Boolean
        Public Property HResult As Integer
        Public Property ErrorText As String
        Public Property AllowedForAgent As Boolean
    End Class

    ''' <summary>
    ''' A read-only, late-bound CrossComm session.
    '''
    ''' Late binding is deliberate. The probe must run on a controller where no
    ''' interop assembly has been generated, and its job is to discover the API
    ''' rather than assume it. Reflection over IDispatch gets us ShowVar and the
    ''' ShowMultiVar experiments without a build-time dependency on the type
    ''' library.
    '''
    ''' Its limitation is equally deliberate to state: callbacks cannot be
    ''' delivered to a late-bound object. Implementing ICKCallbackVar requires a
    ''' real interface with the correct IID and vtable order, which is why
    ''' --dump-typelib exists — it produces exactly the IIDs needed to declare
    ''' those interfaces for the agent.
    '''
    ''' All COM access is marshalled onto the owning <see cref="StaHost"/>.
    ''' </summary>
    Public NotInheritable Class CrossCommSession
        Implements IDisposable

        Private ReadOnly _sta As StaHost
        Private ReadOnly _clientName As String
        Private ReadOnly _callTimeout As TimeSpan
        Private ReadOnly _services As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)

        Private _factory As Object
        Private _disposed As Boolean

        Public ReadOnly Property ClientName As String
            Get
                Return _clientName
            End Get
        End Property

        Public Sub New(sta As StaHost, clientName As String, callTimeout As TimeSpan)
            If sta Is Nothing Then Throw New ArgumentNullException(NameOf(sta))
            If String.IsNullOrWhiteSpace(clientName) Then
                Throw New ArgumentException("Client name must not be empty.", NameOf(clientName))
            End If

            ' Sharing KukavarProxy's client name would make the two register as
            ' the same CrossComm client. cCrossComm.cls:150 stores it verbatim
            ' and passes it to every GetService call.
            If String.Equals(clientName, "KUKAVARPROXY", StringComparison.OrdinalIgnoreCase) Then
                Throw New ArgumentException(
                    "Client name 'KUKAVARPROXY' is reserved by the original proxy. " &
                    "Using it risks colliding with a running KukavarProxy instance.",
                    NameOf(clientName))
            End If

            _sta = sta
            _clientName = clientName
            _callTimeout = callTimeout
        End Sub

        ''' <summary>
        ''' Creates the service factory. Equivalent to cCrossComm.Init
        ''' (cCrossComm.cls:124), which does `Set objServiceFactory = New KrcServiceFactory`.
        ''' </summary>
        Public Sub Connect()
            EnsureNotDisposed()

            _sta.Invoke("CreateServiceFactory",
                Sub()
                    Dim t = Type.GetTypeFromProgID(ServiceIds.ServiceFactoryProgId, throwOnError:=False)

                    If t Is Nothing Then
                        Throw New CrossCommUnavailableException(
                            $"ProgID '{ServiceIds.ServiceFactoryProgId}' is not registered for a 32-bit process. " &
                            "CrossComm is either not installed on this machine, or this process is 64-bit. " &
                            "Verify with: reg query HKCR\CLSID /f KrcServiceFactory /s")
                    End If

                    _factory = Activator.CreateInstance(t)
                End Sub,
                _callTimeout)
        End Sub

        ''' <summary>
        ''' Attempts to obtain every known service and reports which ones the
        ''' controller actually offers.
        '''
        ''' Obtaining a service performs no robot action. Nothing is invoked on
        ''' the returned objects, and non-allowlisted services are released
        ''' immediately.
        ''' </summary>
        Public Function ProbeServices(includeNonAgentServices As Boolean) As List(Of ServiceAvailability)
            EnsureNotDisposed()
            EnsureConnected()

            Dim results As New List(Of ServiceAvailability)
            Dim toProbe = If(includeNonAgentServices, ServiceIds.All, ServiceIds.AgentAllowed)

            For Each progId In toProbe
                Dim allowed = Array.IndexOf(ServiceIds.AgentAllowed, progId) >= 0
                Dim entry As New ServiceAvailability With {
                    .ProgId = progId,
                    .AllowedForAgent = allowed
                }

                Try
                    Dim svc = GetServiceInternal(progId)
                    entry.Available = svc IsNot Nothing

                    If Not allowed Then
                        ' Never cache a service the agent is not allowed to use.
                        ' Report existence, then let it go.
                        ReleaseService(progId)
                    End If

                Catch ex As CrossCommCallException
                    entry.Available = False
                    entry.HResult = ex.HResult32
                    entry.ErrorText = ex.Message
                Catch ex As StaCallTimeoutException
                    entry.Available = False
                    entry.ErrorText = ex.Message
                End Try

                results.Add(entry)
            Next

            Return results
        End Function

        Private Function GetServiceInternal(progId As String) As Object
            Dim cached As Object = Nothing
            If _services.TryGetValue(progId, cached) Then Return cached

            Dim svc = _sta.Invoke($"GetService({progId})",
                Function()
                    Return _factory.GetType().InvokeMember(
                        "GetService",
                        BindingFlags.InvokeMethod,
                        Nothing,
                        _factory,
                        New Object() {progId, _clientName},
                        CultureInfo.InvariantCulture)
                End Function,
                _callTimeout)

            If svc IsNot Nothing Then _services(progId) = svc
            Return svc
        End Function

        Public Function GetService(progId As String) As Object
            EnsureNotDisposed()
            EnsureConnected()
            Return GetServiceInternal(progId)
        End Function

        ''' <summary>
        ''' Reads one variable through ICKSyncVar.ShowVar.
        '''
        ''' The raw return value is passed through untouched. cCrossComm.cls:253
        ''' prepends "name=" to it and basMain.bas:374 then strips everything up
        ''' to the first "=", which corrupts any value that itself contains an
        ''' equals sign. We keep what the controller returned.
        ''' </summary>
        Public Function ShowVar(variableName As String) As String
            EnsureNotDisposed()
            EnsureConnected()

            If String.IsNullOrWhiteSpace(variableName) Then
                Throw New ArgumentException("Variable name must not be empty.", NameOf(variableName))
            End If

            Dim syncVar = GetServiceInternal(ServiceIds.SyncVar)
            If syncVar Is Nothing Then
                Throw New CrossCommUnavailableException(
                    $"Service '{ServiceIds.SyncVar}' is unavailable on this controller.")
            End If

            Dim result = _sta.Invoke($"ShowVar({variableName})",
                Function()
                    Return syncVar.GetType().InvokeMember(
                        "ShowVar",
                        BindingFlags.InvokeMethod,
                        Nothing,
                        syncVar,
                        New Object() {variableName},
                        CultureInfo.InvariantCulture)
                End Function,
                _callTimeout)

            Return If(result Is Nothing, Nothing, Convert.ToString(result, CultureInfo.InvariantCulture))
        End Function

        ''' <summary>
        ''' Attempts a batched read. The signature of ShowMultiVar is not known
        ''' from the KukavarProxy source — only its existence is implied by
        ''' ICKCallbackVar.OnShowMultiVar (cCrossComm.cls:1025) — so this tries
        ''' the plausible marshalling forms in turn and reports which one the
        ''' controller accepted.
        '''
        ''' Answering this is worth real money: 24 variables per sample is 24
        ''' COM round trips with ShowVar and 1 with ShowMultiVar.
        ''' </summary>
        Public Function TryShowMultiVar(variableNames As String(),
                                        ByRef attemptLog As List(Of String)) As Object
            EnsureNotDisposed()
            EnsureConnected()

            If variableNames Is Nothing OrElse variableNames.Length = 0 Then
                Throw New ArgumentException("At least one variable name is required.", NameOf(variableNames))
            End If

            attemptLog = New List(Of String)

            Dim syncVar = GetServiceInternal(ServiceIds.SyncVar)
            If syncVar Is Nothing Then
                Throw New CrossCommUnavailableException(
                    $"Service '{ServiceIds.SyncVar}' is unavailable on this controller.")
            End If

            ' Candidate argument shapes, most likely first.
            Dim candidates As New List(Of Tuple(Of String, Object()))(New Tuple(Of String, Object())() {
                Tuple.Create("String() as SAFEARRAY(BSTR)", New Object() {variableNames}),
                Tuple.Create("Object() of String", New Object() {variableNames.Cast(Of Object).ToArray()}),
                Tuple.Create("comma-separated single BSTR", New Object() {String.Join(",", variableNames)}),
                Tuple.Create("newline-separated single BSTR", New Object() {String.Join(vbLf, variableNames)})
            })

            For Each candidate In candidates
                Try
                    Dim value = _sta.Invoke($"ShowMultiVar[{candidate.Item1}]",
                        Function()
                            Return syncVar.GetType().InvokeMember(
                                "ShowMultiVar",
                                BindingFlags.InvokeMethod,
                                Nothing,
                                syncVar,
                                candidate.Item2,
                                CultureInfo.InvariantCulture)
                        End Function,
                        _callTimeout)

                    attemptLog.Add($"OK    {candidate.Item1} -> {DescribeValue(value)}")
                    Return value

                Catch ex As CrossCommCallException
                    ' StaHost wraps every failure raised on the pump thread, so
                    ' "member does not exist" arrives here rather than as a bare
                    ' MissingMethodException. That distinction matters: an absent
                    ' member means stop trying, a failed call means try the next
                    ' marshalling.
                    If IsMemberNotFound(ex) Then
                        attemptLog.Add($"ABSENT {candidate.Item1}: 'ShowMultiVar' is not a member of ICKSyncVar")
                        Return Nothing
                    End If

                    attemptLog.Add($"FAIL  {candidate.Item1}: 0x{ex.HResult32:X8} {ex.Message}")

                Catch ex As StaCallTimeoutException
                    attemptLog.Add($"TIMEOUT {candidate.Item1}")
                    Throw
                End Try
            Next

            Return Nothing
        End Function

        ''' <summary>
        ''' Distinguishes "this member does not exist" from "the call failed".
        ''' Late binding reports the former either as MissingMemberException or
        ''' as DISP_E_UNKNOWNNAME / DISP_E_MEMBERNOTFOUND from IDispatch.
        ''' </summary>
        Private Shared Function IsMemberNotFound(ex As CrossCommCallException) As Boolean
            Const DISP_E_UNKNOWNNAME As Integer = &H80020006
            Const DISP_E_MEMBERNOTFOUND As Integer = &H80020003

            If ex.HResult32 = DISP_E_UNKNOWNNAME OrElse
               ex.HResult32 = DISP_E_MEMBERNOTFOUND Then
                Return True
            End If

            Dim inner = ex.InnerException
            While inner IsNot Nothing
                If TypeOf inner Is MissingMemberException Then Return True
                inner = inner.InnerException
            End While

            Return False
        End Function

        Private Shared Function DescribeValue(value As Object) As String
            If value Is Nothing Then Return "(Nothing)"

            Dim arr = TryCast(value, Array)
            If arr IsNot Nothing Then
                Dim items = arr.Cast(Of Object).
                               Take(8).
                               Select(Function(o) If(o Is Nothing, "(null)", o.ToString()))
                Dim suffix = If(arr.Length > 8, $", … ({arr.Length} total)", "")
                Return $"{value.GetType().Name}[{arr.Length}] {{{String.Join(" | ", items)}{suffix}}}"
            End If

            Return $"{value.GetType().Name}: {value}"
        End Function

        Private Sub ReleaseService(progId As String)
            Dim svc As Object = Nothing
            If Not _services.TryGetValue(progId, svc) Then Return
            _services.Remove(progId)

            _sta.Post(Sub()
                          Dim tmp = svc
                          NativeMethods.FinalRelease(tmp)
                      End Sub)
        End Sub

        Private Sub EnsureConnected()
            If _factory Is Nothing Then
                Throw New InvalidOperationException("Connect() must be called before using the session.")
            End If
        End Sub

        Private Sub EnsureNotDisposed()
            If _disposed Then Throw New ObjectDisposedException(NameOf(CrossCommSession))
        End Sub

        ''' <summary>
        ''' Releases every COM reference on the owning STA thread, in reverse
        ''' order of acquisition. This mirrors cCrossComm.ServerOff
        ''' (cCrossComm.cls:219). Skipping it leaves the CrossComm server
        ''' holding a client registration that no longer exists, which on a
        ''' controller accumulates across restarts.
        ''' </summary>
        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True

            Dim toRelease = _services.Values.Reverse().ToList()
            _services.Clear()

            Dim factory = _factory
            _factory = Nothing

            If _sta.IsStuck Then
                ' The pump thread is blocked; queuing work would never run and
                ' waiting would hang shutdown. The process is about to die and
                ' the OS will reclaim the references.
                Return
            End If

            Try
                _sta.Invoke("ReleaseComObjects",
                    Sub()
                        For Each svc In toRelease
                            Dim tmp = svc
                            NativeMethods.FinalRelease(tmp)
                        Next
                        Dim f = factory
                        NativeMethods.FinalRelease(f)
                    End Sub,
                    TimeSpan.FromSeconds(5))
            Catch ex As StaCallTimeoutException
                ' Shutdown is best-effort by definition.
            Catch ex As InvalidOperationException
                ' Host already torn down.
            End Try
        End Sub

    End Class

End Namespace
