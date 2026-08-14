Imports System.Runtime.InteropServices
Imports System.Runtime.InteropServices.ComTypes

Namespace Interop

    ''' <summary>
    ''' Wraps a failure raised by a CrossComm call, preserving the operation
    ''' name and, where available, the underlying HRESULT.
    ''' </summary>
    Public Class CrossCommCallException
        Inherits Exception

        Public ReadOnly Property Operation As String
        Public ReadOnly Property HResult32 As Integer

        Public Sub New(operation As String, inner As Exception)
            MyBase.New(BuildMessage(operation, inner), inner)
            Me.Operation = operation

            ' Type.InvokeMember wraps whatever the COM object threw in a
            ' TargetInvocationException, so the HRESULT is one or more levels
            ' down. Looking only at the direct inner exception loses it.
            Me.HResult32 = FindHResult(inner)
        End Sub

        Private Shared Function FindHResult(ex As Exception) As Integer
            Dim current = ex
            While current IsNot Nothing
                Dim com = TryCast(current, COMException)
                If com IsNot Nothing Then Return com.ErrorCode
                current = current.InnerException
            End While
            Return 0
        End Function

        Private Shared Function BuildMessage(operation As String, inner As Exception) As String
            Dim com = TryCast(inner, COMException)
            If com IsNot Nothing Then
                Return $"CrossComm call '{operation}' failed with HRESULT 0x{com.ErrorCode:X8}: {com.Message}"
            End If

            Dim tie = TryCast(inner, Reflection.TargetInvocationException)
            If tie?.InnerException IsNot Nothing Then
                Return BuildMessage(operation, tie.InnerException)
            End If

            Return $"CrossComm call '{operation}' failed: {inner.Message}"
        End Function
    End Class

    ''' <summary>
    ''' Raised when a required CrossComm service cannot be obtained, which in
    ''' practice means the library is missing, unregistered, or the process is
    ''' running as 64-bit.
    ''' </summary>
    Public Class CrossCommUnavailableException
        Inherits Exception

        Public Sub New(message As String, Optional inner As Exception = Nothing)
            MyBase.New(message, inner)
        End Sub
    End Class

    Friend NotInheritable Class NativeMethods

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Type library identity of the KUKA Cross KRC Library, taken from the
        ''' VB6 project reference in KukavarProxy.vbp line 3:
        '''
        '''   Reference=*\G{307230E0-B48F-11D4-B053-00A0D21AFA30}#1.5#0#
        '''             ..\Cross3Krc.CIE#KUKA Cross KRC Library
        ''' </summary>
        Public Shared ReadOnly CrossKrcTypeLibId As New Guid("307230E0-B48F-11D4-B053-00A0D21AFA30")

        Public Const CrossKrcMajorVersion As Short = 1S
        Public Const CrossKrcMinorVersion As Short = 5S

        ''' <summary>Loads a type library from an explicit file path.</summary>
        <DllImport("oleaut32.dll", CharSet:=CharSet.Unicode, PreserveSig:=False)>
        Public Shared Sub LoadTypeLib(
            <MarshalAs(UnmanagedType.LPWStr)> fileName As String,
            <MarshalAs(UnmanagedType.Interface)> ByRef typeLib As ITypeLib)
        End Sub

        ''' <summary>
        ''' Loads a registered type library by GUID and version. Fails if the
        ''' library is not registered in the current process bitness view.
        ''' </summary>
        <DllImport("oleaut32.dll", CharSet:=CharSet.Unicode, PreserveSig:=False)>
        Public Shared Sub LoadRegTypeLib(
            ByRef libId As Guid,
            majorVersion As Short,
            minorVersion As Short,
            lcid As Integer,
            <MarshalAs(UnmanagedType.Interface)> ByRef typeLib As ITypeLib)
        End Sub

        ''' <summary>
        ''' Resolves the on-disk path of a registered type library.
        ''' </summary>
        <DllImport("oleaut32.dll", CharSet:=CharSet.Unicode, PreserveSig:=False)>
        Public Shared Sub QueryPathOfRegTypeLib(
            ByRef libId As Guid,
            majorVersion As Short,
            minorVersion As Short,
            lcid As Integer,
            <MarshalAs(UnmanagedType.BStr)> ByRef path As String)
        End Sub

        ''' <summary>
        ''' Releases a COM object and reports how many references remained.
        ''' Safe to call with Nothing or with a non-COM object.
        ''' </summary>
        Public Shared Function SafeRelease(ByRef obj As Object) As Integer
            If obj Is Nothing Then Return -1

            Dim remaining As Integer = -1
            Try
                If Marshal.IsComObject(obj) Then
                    remaining = Marshal.ReleaseComObject(obj)
                End If
            Catch ex As ArgumentException
                ' Not an RCW; nothing to release.
            Finally
                obj = Nothing
            End Try

            Return remaining
        End Function

        ''' <summary>
        ''' Releases a COM object repeatedly until its reference count reaches
        ''' zero. Used only at shutdown, where a leaked reference would keep the
        ''' CrossComm server holding a stale client registration.
        ''' </summary>
        Public Shared Sub FinalRelease(ByRef obj As Object)
            If obj Is Nothing Then Return

            Try
                If Marshal.IsComObject(obj) Then
                    Marshal.FinalReleaseComObject(obj)
                End If
            Catch ex As ArgumentException
                ' Not an RCW.
            Finally
                obj = Nothing
            End Try
        End Sub

    End Class

End Namespace
