Imports System.Threading
Imports System.Windows.Forms

Namespace Interop

    ''' <summary>
    ''' Raised when a call marshalled onto the STA thread did not return within
    ''' its timeout. The STA thread is left running but is considered stuck:
    ''' there is no supported way to abort a blocked cross-apartment COM call.
    ''' </summary>
    Public Class StaCallTimeoutException
        Inherits TimeoutException

        Public ReadOnly Property Operation As String

        Public Sub New(operation As String, timeout As TimeSpan)
            MyBase.New($"Operation '{operation}' did not complete within {timeout.TotalMilliseconds:F0} ms. " &
                       "The STA thread is blocked inside CrossComm and cannot be recovered.")
            Me.Operation = operation
        End Sub
    End Class

    ''' <summary>
    ''' Owns a single-threaded apartment (STA) thread with a running Windows
    ''' message pump, and marshals work onto it.
    '''
    ''' WHY THIS EXISTS
    ''' ---------------
    ''' Every WBC_KrcLib object must be created, used and released on one STA
    ''' thread, and that thread must pump messages. Two independent reasons:
    '''
    ''' 1. Apartment affinity. The CrossComm objects are apartment-threaded.
    '''    Touching a raw interface pointer from another thread is undefined
    '''    behaviour; .NET normally hides this by marshalling through a proxy,
    '''    but the proxy only works if the owning apartment pumps messages.
    '''
    ''' 2. Callback delivery. ICKCallbackVar.OnSetInfo and
    '''    ICKConsumeMessage.OnAddMessage are delivered by COM as window
    '''    messages posted to the apartment's hidden OLE window. A thread that
    '''    does not pump will simply never receive them — no error, no
    '''    exception, just silence. This is the single most common reason
    '''    people fail to get CrossComm subscriptions working from a service.
    '''
    ''' KukavarProxy sidesteps all of this by being a VB6 form application:
    ''' cCrossComm.Init() takes the form as its parent (cCrossComm.cls:118) and
    ''' the VB6 runtime pumps messages for free. A console application or a
    ''' Windows service gets no pump by default and must create one explicitly.
    '''
    ''' SHUTDOWN
    ''' --------
    ''' The thread is deliberately NOT a background thread. COM objects must be
    ''' released on their owning apartment; letting the CLR tear down a
    ''' background thread at process exit skips that and leaves the CrossComm
    ''' server holding stale client references. Callers must Dispose.
    ''' </summary>
    Public NotInheritable Class StaHost
        Implements IDisposable

        Private ReadOnly _thread As Thread
        Private ReadOnly _ready As New ManualResetEventSlim(False)
        Private ReadOnly _syncRoot As New Object()

        Private _marshaller As Control
        Private _startupError As Exception
        Private _stuck As Boolean
        Private _disposed As Boolean

        ''' <summary>
        ''' True once a marshalled call has exceeded its timeout. The host is
        ''' unusable from this point on: the pump thread is blocked inside COM
        ''' and every subsequent call would queue behind it forever.
        ''' </summary>
        Public ReadOnly Property IsStuck As Boolean
            Get
                Return Volatile.Read(_stuck)
            End Get
        End Property

        Public ReadOnly Property ManagedThreadId As Integer
            Get
                Return _thread.ManagedThreadId
            End Get
        End Property

        Public Sub New(name As String)
            _thread = New Thread(AddressOf PumpThreadProc) With {
                .Name = name,
                .IsBackground = False
            }
            _thread.SetApartmentState(ApartmentState.STA)
            _thread.Start()

            _ready.Wait()

            If _startupError IsNot Nothing Then
                Throw New InvalidOperationException(
                    "Failed to start the STA host thread.", _startupError)
            End If
        End Sub

        Private Sub PumpThreadProc()
            Try
                ' Creating the Control is not enough — the window handle is
                ' created lazily. Touching .Handle forces it now, on this
                ' thread, which is what binds the marshaller to this apartment.
                _marshaller = New Control()
                Dim unused = _marshaller.Handle

                _ready.Set()

                ' Blocks until ExitThread is posted from Dispose.
                Application.Run(New ApplicationContext())

            Catch ex As Exception
                _startupError = ex
                _ready.Set()
            End Try
        End Sub

        ''' <summary>
        ''' Runs <paramref name="func"/> on the STA thread and returns its result.
        ''' </summary>
        ''' <exception cref="StaCallTimeoutException">
        ''' The call did not return in time. The host is marked stuck.
        ''' </exception>
        Public Function Invoke(Of T)(operation As String,
                                     func As Func(Of T),
                                     timeout As TimeSpan) As T
            If func Is Nothing Then Throw New ArgumentNullException(NameOf(func))
            EnsureUsable(operation)

            ' Fast path: already on the pump thread (e.g. called from a callback).
            If Thread.CurrentThread Is _thread Then
                Return func()
            End If

            Dim result As T = Nothing
            Dim failure As Exception = Nothing

            Dim work As New Action(
                Sub()
                    Try
                        result = func()
                    Catch ex As Exception
                        failure = ex
                    End Try
                End Sub)

            ' 'Async' is a VB modifier keyword, so the handle is not named that.
            Dim pending = _marshaller.BeginInvoke(work)

            If Not pending.AsyncWaitHandle.WaitOne(timeout) Then
                ' Do not call EndInvoke — it would block forever. Do not abort
                ' the thread either: aborting a thread inside a COM call
                ' corrupts the apartment and can take the CrossComm server
                ' down with it. Mark and surface.
                Volatile.Write(_stuck, True)
                Throw New StaCallTimeoutException(operation, timeout)
            End If

            _marshaller.EndInvoke(pending)

            If failure IsNot Nothing Then
                Throw New CrossCommCallException(operation, failure)
            End If

            Return result
        End Function

        ''' <summary>Runs an action on the STA thread and waits for it.</summary>
        Public Sub Invoke(operation As String, action As Action, timeout As TimeSpan)
            If action Is Nothing Then Throw New ArgumentNullException(NameOf(action))
            Invoke(operation,
                   Function()
                       action()
                       Return True
                   End Function,
                   timeout)
        End Sub

        ''' <summary>
        ''' Queues an action on the STA thread without waiting. Used for work
        ''' that must not block the caller, such as releasing objects during a
        ''' best-effort shutdown.
        ''' </summary>
        Public Sub Post(action As Action)
            If action Is Nothing Then Throw New ArgumentNullException(NameOf(action))
            SyncLock _syncRoot
                If _disposed OrElse _marshaller Is Nothing Then Return
            End SyncLock

            Try
                _marshaller.BeginInvoke(action)
            Catch ex As InvalidOperationException
                ' Handle already destroyed; nothing to do.
            End Try
        End Sub

        Private Sub EnsureUsable(operation As String)
            SyncLock _syncRoot
                If _disposed Then
                    Throw New ObjectDisposedException(NameOf(StaHost))
                End If
            End SyncLock

            If IsStuck Then
                Throw New InvalidOperationException(
                    $"Cannot run '{operation}': the STA thread is blocked inside a previous " &
                    "CrossComm call. The process must be restarted to recover.")
            End If
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            SyncLock _syncRoot
                If _disposed Then Return
                _disposed = True
            End SyncLock

            If _marshaller IsNot Nothing Then
                Try
                    _marshaller.BeginInvoke(New Action(Sub() Application.ExitThread()))
                Catch ex As InvalidOperationException
                    ' Handle gone; the pump is already stopping.
                End Try
            End If

            ' A stuck thread will never honour ExitThread. Waiting forever here
            ' would turn a diagnosable hang into an unkillable process, so the
            ' join is bounded and failure to stop is reported by the caller.
            If Not _thread.Join(TimeSpan.FromSeconds(5)) Then
                Volatile.Write(_stuck, True)
            End If

            _ready.Dispose()
        End Sub

    End Class

End Namespace
