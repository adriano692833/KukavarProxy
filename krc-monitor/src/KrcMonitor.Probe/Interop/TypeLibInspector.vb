Imports System.Globalization
Imports System.Runtime.InteropServices
Imports System.Runtime.InteropServices.ComTypes
Imports System.Text

Namespace Interop

    Public Class TypeMemberInfo
        Public Property Name As String
        Public Property MemberId As Integer
        Public Property InvokeKind As INVOKEKIND
        Public Property ReturnType As String
        Public Property Parameters As New List(Of String)
        Public Property VTableOffset As Integer

        Public Overrides Function ToString() As String
            Return ToSignature()
        End Function

        Public Function ToSignature() As String
            Dim sb As New StringBuilder()

            Select Case InvokeKind
                Case INVOKEKIND.INVOKE_PROPERTYGET : sb.Append("[get] ")
                Case INVOKEKIND.INVOKE_PROPERTYPUT : sb.Append("[put] ")
                Case INVOKEKIND.INVOKE_PROPERTYPUTREF : sb.Append("[putref] ")
            End Select

            Dim isSub = String.Equals(ReturnType, "Void", StringComparison.Ordinal)
            sb.Append(If(isSub, "Sub ", "Function "))
            sb.Append(Name)
            sb.Append("("c)
            sb.Append(String.Join(", ", Parameters))
            sb.Append(")"c)

            If Not isSub Then
                sb.Append(" As ").Append(ReturnType)
            End If

            Return sb.ToString()
        End Function
    End Class

    Public Class TypeEntryInfo
        Public Property Name As String
        Public Property DocString As String
        Public Property Kind As TYPEKIND
        Public Property Iid As Guid
        Public Property Members As New List(Of TypeMemberInfo)
        Public Property ImplementedInterfaces As New List(Of String)
    End Class

    Public Class TypeLibraryInfo
        Public Property Name As String
        Public Property DocString As String
        Public Property LibId As Guid
        Public Property MajorVersion As Short
        Public Property MinorVersion As Short
        Public Property Path As String
        Public Property Types As New List(Of TypeEntryInfo)
    End Class

    ''' <summary>
    ''' Reads the WBC_KrcLib type library directly through ITypeLib/ITypeInfo
    ''' and reports its real interfaces, methods and IIDs.
    '''
    ''' WHY NOT tlbimp
    ''' --------------
    ''' tlbimp produces an interop assembly, which is what the production agent
    ''' will use. But it has to run on a machine where Cross3Krc.CIE is present,
    ''' and it answers only the questions its output happens to expose.
    '''
    ''' The probe has a different job: establish what the API actually offers on
    ''' THIS controller, including the questions the KukavarProxy source cannot
    ''' answer. Specifically:
    '''
    '''   - Does ICKSyncVar really expose ShowMultiVar? cCrossComm.cls never
    '''     calls it, but ICKCallbackVar declares OnShowMultiVar
    '''     (cCrossComm.cls:1025), so the method almost certainly exists. Its
    '''     signature decides whether batched reads are possible at all, which
    '''     is the difference between 1 and N COM round trips per sample.
    '''
    '''   - What are the IIDs of ICKCallbackVar and ICKConsumeMessage? The agent
    '''     must implement both, and hand-declaring a ComImport interface
    '''     requires the exact IID and the exact vtable order.
    '''
    ''' Reading the type library answers both without guessing, and works even
    ''' where tlbimp is not installed.
    '''
    ''' This inspector performs no CoCreateInstance and touches no CrossComm
    ''' object. It is pure metadata and cannot affect the controller.
    ''' </summary>
    Public NotInheritable Class TypeLibInspector

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Loads the library by registered GUID, falling back to an explicit
        ''' file path when the registry lookup fails.
        ''' </summary>
        Public Shared Function Load(Optional explicitPath As String = Nothing) As TypeLibraryInfo
            Dim lib As ITypeLib = Nothing
            Dim resolvedPath As String = explicitPath

            If String.IsNullOrEmpty(explicitPath) Then
                Dim libId = NativeMethods.CrossKrcTypeLibId
                Try
                    NativeMethods.LoadRegTypeLib(libId,
                                                 NativeMethods.CrossKrcMajorVersion,
                                                 NativeMethods.CrossKrcMinorVersion,
                                                 0, lib)
                Catch ex As COMException
                    Throw New CrossCommUnavailableException(
                        $"Type library {libId:B} v{NativeMethods.CrossKrcMajorVersion}." &
                        $"{NativeMethods.CrossKrcMinorVersion} is not registered for a 32-bit process " &
                        "(HRESULT 0x" & ex.ErrorCode.ToString("X8", CultureInfo.InvariantCulture) & "). " &
                        "Either CrossComm is not installed, or the process is running as 64-bit, " &
                        "or the library is registered only under the 64-bit registry view. " &
                        "Pass --typelib <path to Cross3Krc.CIE> to load it directly.", ex)
                End Try

                Try
                    NativeMethods.QueryPathOfRegTypeLib(libId,
                                                        NativeMethods.CrossKrcMajorVersion,
                                                        NativeMethods.CrossKrcMinorVersion,
                                                        0, resolvedPath)
                Catch ex As COMException
                    resolvedPath = "(registered, path unavailable)"
                End Try
            Else
                Try
                    NativeMethods.LoadTypeLib(explicitPath, lib)
                Catch ex As COMException
                    Throw New CrossCommUnavailableException(
                        $"Failed to load type library from '{explicitPath}' " &
                        "(HRESULT 0x" & ex.ErrorCode.ToString("X8", CultureInfo.InvariantCulture) & ").", ex)
                End Try
            End If

            Try
                Return Describe(lib, resolvedPath)
            Finally
                Dim tmp As Object = lib
                NativeMethods.SafeRelease(tmp)
            End Try
        End Function

        Private Shared Function Describe(lib As ITypeLib, path As String) As TypeLibraryInfo
            Dim info As New TypeLibraryInfo With {.Path = path}

            Dim libName As String = Nothing, libDoc As String = Nothing
            Dim helpCtx As Integer, helpFile As String = Nothing
            lib.GetDocumentation(-1, libName, libDoc, helpCtx, helpFile)
            info.Name = libName
            info.DocString = libDoc

            Dim attrPtr As IntPtr = IntPtr.Zero
            Try
                lib.GetLibAttr(attrPtr)
                Dim attr = CType(Marshal.PtrToStructure(attrPtr, GetType(TYPELIBATTR)), TYPELIBATTR)
                info.LibId = attr.guid
                info.MajorVersion = attr.wMajorVerNum
                info.MinorVersion = attr.wMinorVerNum
            Finally
                If attrPtr <> IntPtr.Zero Then lib.ReleaseTLibAttr(attrPtr)
            End Try

            Dim count = lib.GetTypeInfoCount()
            For i = 0 To count - 1
                Dim ti As ITypeInfo = Nothing
                Try
                    lib.GetTypeInfo(i, ti)
                    info.Types.Add(DescribeType(lib, ti, i))
                Catch ex As COMException
                    info.Types.Add(New TypeEntryInfo With {
                        .Name = $"(entry {i}: unreadable, HRESULT 0x{ex.ErrorCode:X8})",
                        .Kind = TYPEKIND.TKIND_MAX
                    })
                Finally
                    Dim tmp As Object = ti
                    NativeMethods.SafeRelease(tmp)
                End Try
            Next

            Return info
        End Function

        Private Shared Function DescribeType(lib As ITypeLib, ti As ITypeInfo, index As Integer) As TypeEntryInfo
            Dim entry As New TypeEntryInfo()

            Dim name As String = Nothing, doc As String = Nothing
            Dim helpCtx As Integer, helpFile As String = Nothing
            lib.GetDocumentation(index, name, doc, helpCtx, helpFile)
            entry.Name = name
            entry.DocString = doc

            Dim attrPtr As IntPtr = IntPtr.Zero
            Try
                ti.GetTypeAttr(attrPtr)
                Dim attr = CType(Marshal.PtrToStructure(attrPtr, GetType(TYPEATTR)), TYPEATTR)

                entry.Iid = attr.guid
                entry.Kind = attr.typekind

                For f = 0 To attr.cFuncs - 1
                    Dim member = DescribeFunction(ti, f)
                    If member IsNot Nothing Then entry.Members.Add(member)
                Next

                For h = 0 To attr.cImplTypes - 1
                    Try
                        Dim href As Integer
                        ti.GetRefTypeOfImplType(h, href)
                        Dim parent As ITypeInfo = Nothing
                        Try
                            ti.GetRefTypeInfo(href, parent)
                            Dim pName As String = Nothing, pDoc As String = Nothing
                            Dim pCtx As Integer, pFile As String = Nothing
                            parent.GetDocumentation(-1, pName, pDoc, pCtx, pFile)
                            entry.ImplementedInterfaces.Add(pName)
                        Finally
                            Dim tmp As Object = parent
                            NativeMethods.SafeRelease(tmp)
                        End Try
                    Catch ex As COMException
                        entry.ImplementedInterfaces.Add($"(unresolved, 0x{ex.ErrorCode:X8})")
                    End Try
                Next

            Finally
                If attrPtr <> IntPtr.Zero Then ti.ReleaseTypeAttr(attrPtr)
            End Try

            Return entry
        End Function

        Private Shared Function DescribeFunction(ti As ITypeInfo, index As Integer) As TypeMemberInfo
            Dim descPtr As IntPtr = IntPtr.Zero
            Try
                ti.GetFuncDesc(index, descPtr)
                Dim fd = CType(Marshal.PtrToStructure(descPtr, GetType(FUNCDESC)), FUNCDESC)

                Dim member As New TypeMemberInfo With {
                    .MemberId = fd.memid,
                    .InvokeKind = fd.invkind,
                    .VTableOffset = fd.oVft,
                    .ReturnType = TypeDescToString(ti, fd.elemdescFunc.tdesc)
                }

                ' Element 0 of GetNames is the method name; the rest are
                ' parameter names, in declaration order.
                Dim names(fd.cParams) As String
                Dim fetched As Integer
                ti.GetNames(fd.memid, names, names.Length, fetched)
                member.Name = If(fetched > 0, names(0), $"(memid {fd.memid})")

                Dim elemSize = Marshal.SizeOf(GetType(ELEMDESC))
                For p = 0 To fd.cParams - 1
                    Dim elemPtr = New IntPtr(fd.lprgelemdescParam.ToInt64() + CLng(p) * elemSize)
                    Dim ed = CType(Marshal.PtrToStructure(elemPtr, GetType(ELEMDESC)), ELEMDESC)

                    Dim pName = If(p + 1 < fetched AndAlso names(p + 1) IsNot Nothing,
                                   names(p + 1), $"arg{p}")
                    Dim flags = DescribeParamFlags(ed.desc.paramdesc.wParamFlags)
                    member.Parameters.Add($"{flags}{pName} As {TypeDescToString(ti, ed.tdesc)}")
                Next

                Return member

            Catch ex As COMException
                Return New TypeMemberInfo With {
                    .Name = $"(function {index}: unreadable, HRESULT 0x{ex.ErrorCode:X8})"
                }
            Finally
                If descPtr <> IntPtr.Zero Then ti.ReleaseFuncDesc(descPtr)
            End Try
        End Function

        Private Shared Function DescribeParamFlags(flags As PARAMFLAG) As String
            Dim parts As New List(Of String)
            If (flags And PARAMFLAG.PARAMFLAG_FIN) <> 0 Then parts.Add("in")
            If (flags And PARAMFLAG.PARAMFLAG_FOUT) <> 0 Then parts.Add("out")
            If (flags And PARAMFLAG.PARAMFLAG_FOPT) <> 0 Then parts.Add("optional")
            If (flags And PARAMFLAG.PARAMFLAG_FRETVAL) <> 0 Then parts.Add("retval")
            Return If(parts.Count = 0, "", "<" & String.Join(",", parts) & "> ")
        End Function

        ''' <summary>
        ''' Renders a TYPEDESC as a readable type name, following pointers,
        ''' safe arrays and user-defined type references.
        ''' </summary>
        Private Shared Function TypeDescToString(ti As ITypeInfo, td As TYPEDESC) As String
            Dim vt = CType(td.vt, VarEnum)

            Select Case vt
                Case VarEnum.VT_PTR
                    Dim inner = CType(Marshal.PtrToStructure(td.lpValue, GetType(TYPEDESC)), TYPEDESC)
                    Return TypeDescToString(ti, inner) & "*"

                Case VarEnum.VT_SAFEARRAY
                    Dim inner = CType(Marshal.PtrToStructure(td.lpValue, GetType(TYPEDESC)), TYPEDESC)
                    Return "SAFEARRAY(" & TypeDescToString(ti, inner) & ")"

                Case VarEnum.VT_CARRAY
                    Return "CARRAY"

                Case VarEnum.VT_USERDEFINED
                    Try
                        Dim href = td.lpValue.ToInt32()
                        Dim ref As ITypeInfo = Nothing
                        Try
                            ti.GetRefTypeInfo(href, ref)
                            Dim n As String = Nothing, d As String = Nothing
                            Dim c As Integer, f As String = Nothing
                            ref.GetDocumentation(-1, n, d, c, f)
                            Return n
                        Finally
                            Dim tmp As Object = ref
                            NativeMethods.SafeRelease(tmp)
                        End Try
                    Catch ex As COMException
                        Return "(userdefined)"
                    End Try

                Case Else
                    Return VarEnumToString(vt)
            End Select
        End Function

        Private Shared Function VarEnumToString(vt As VarEnum) As String
            Select Case vt
                Case VarEnum.VT_EMPTY : Return "Void"
                Case VarEnum.VT_VOID : Return "Void"
                Case VarEnum.VT_HRESULT : Return "HRESULT"
                Case VarEnum.VT_I1 : Return "SByte"
                Case VarEnum.VT_I2 : Return "Short"
                Case VarEnum.VT_I4 : Return "Integer"
                Case VarEnum.VT_I8 : Return "Long"
                Case VarEnum.VT_INT : Return "Integer"
                Case VarEnum.VT_UI1 : Return "Byte"
                Case VarEnum.VT_UI2 : Return "UShort"
                Case VarEnum.VT_UI4 : Return "UInteger"
                Case VarEnum.VT_UI8 : Return "ULong"
                Case VarEnum.VT_UINT : Return "UInteger"
                Case VarEnum.VT_R4 : Return "Single"
                Case VarEnum.VT_R8 : Return "Double"
                Case VarEnum.VT_BOOL : Return "Boolean"
                Case VarEnum.VT_BSTR : Return "String"
                Case VarEnum.VT_VARIANT : Return "Variant"
                Case VarEnum.VT_DISPATCH : Return "IDispatch"
                Case VarEnum.VT_UNKNOWN : Return "IUnknown"
                Case VarEnum.VT_DATE : Return "Date"
                Case VarEnum.VT_CY : Return "Currency"
                Case VarEnum.VT_DECIMAL : Return "Decimal"
                Case Else : Return vt.ToString()
            End Select
        End Function

    End Class

End Namespace
