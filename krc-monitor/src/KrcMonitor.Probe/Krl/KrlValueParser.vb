Imports System.Globalization
Imports System.Text
Imports System.Text.RegularExpressions

Namespace Krl

    ''' <summary>
    ''' Member names avoid Structure, Enum and Integer, which are VB keywords
    ''' and would need bracket escaping at every use site.
    ''' </summary>
    Public Enum KrlValueKind
        Unknown = 0
        Struct
        EnumValue
        Bool
        Int
        Real
        Text
    End Enum

    Public Class KrlValue
        Public Property Raw As String
        Public Property Kind As KrlValueKind
        Public Property StructTypeName As String
        Public Property Fields As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
        Public Property Scalar As Object

        Public ReadOnly Property IsStructure As Boolean
            Get
                Return Kind = KrlValueKind.Struct
            End Get
        End Property

        Public Overrides Function ToString() As String
            If Not IsStructure Then
                Return $"{Kind}: {FormatScalar(Scalar)}"
            End If

            Dim parts = Fields.Select(Function(kv) $"{kv.Key}={FormatScalar(kv.Value)}")
            Dim prefix = If(String.IsNullOrEmpty(StructTypeName), "", StructTypeName & " ")
            Return $"{prefix}{{{String.Join(", ", parts)}}}"
        End Function

        Public Shared Function FormatScalar(v As Object) As String
            If v Is Nothing Then Return "(null)"
            If TypeOf v Is Double Then
                Return CDbl(v).ToString("0.###", CultureInfo.InvariantCulture)
            End If
            If TypeOf v Is KrlValue Then
                Return DirectCast(v, KrlValue).ToString()
            End If
            Return Convert.ToString(v, CultureInfo.InvariantCulture)
        End Function
    End Class

    ''' <summary>
    ''' Parses the textual form that ICKSyncVar.ShowVar returns.
    '''
    ''' CrossComm hands back the same rendering the teach pendant shows in
    ''' Display &gt; Variable &gt; Single, so the shapes are:
    '''
    '''   structure  {E6POS: X 1075.0, Y 0.0, Z 1409.0, A 0.0, B 90.0, C 0.0, S 2, T 35}
    '''   structure  {X 1075.0, Y 0.0, Z 1409.0}          (type prefix omitted)
    '''   enum       #T1
    '''   bool       TRUE
    '''   integer    100
    '''   real       1075.0
    '''   text       "some text"
    '''
    ''' Numbers always use '.' as the decimal separator regardless of the
    ''' controller's locale, so every conversion here is invariant-culture.
    ''' Parsing with the current culture on a Polish-locale machine would turn
    ''' 1075.0 into 10750, silently and with no exception — exactly the class of
    ''' bug that is impossible to spot on a dashboard.
    '''
    ''' The exact formats must be confirmed against a real controller in phase 1
    ''' (docs/02-crosscomm-api.md section 12, question 1). Anything unrecognised
    ''' degrades to Text with the raw string preserved, never to a wrong number.
    ''' </summary>
    Public NotInheritable Class KrlValueParser

        Private Sub New()
        End Sub

        Private Shared ReadOnly StructPrefix As New Regex(
            "^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*",
            RegexOptions.Compiled Or RegexOptions.CultureInvariant)

        Public Shared Function Parse(raw As String) As KrlValue
            Dim result As New KrlValue With {.Raw = raw}

            If raw Is Nothing Then
                result.Kind = KrlValueKind.Unknown
                Return result
            End If

            Dim text = raw.Trim()
            If text.Length = 0 Then
                result.Kind = KrlValueKind.Unknown
                Return result
            End If

            If text.StartsWith("{", StringComparison.Ordinal) Then
                ParseStructure(text, result)
            Else
                ParseScalarInto(text, result)
            End If

            Return result
        End Function

        Private Shared Sub ParseStructure(text As String, result As KrlValue)
            result.Kind = KrlValueKind.Struct

            Dim closing = text.LastIndexOf("}"c)
            Dim body = If(closing > 0, text.Substring(1, closing - 1), text.Substring(1))

            Dim prefix = StructPrefix.Match(body)
            If prefix.Success Then
                result.StructTypeName = prefix.Groups("name").Value
                body = body.Substring(prefix.Length)
            End If

            For Each token In SplitTopLevel(body)
                Dim trimmed = token.Trim()
                If trimmed.Length = 0 Then Continue For

                ' Fields are "NAME VALUE" separated by whitespace. A nested
                ' structure keeps its braces and is parsed recursively.
                Dim sep = trimmed.IndexOfAny(New Char() {" "c, ControlChars.Tab})
                If sep < 0 Then
                    result.Fields(trimmed) = Nothing
                    Continue For
                End If

                Dim name = trimmed.Substring(0, sep).Trim()
                Dim value = trimmed.Substring(sep + 1).Trim()
                result.Fields(name) = ParseFieldValue(value)
            Next
        End Sub

        Private Shared Function ParseFieldValue(value As String) As Object
            If value.StartsWith("{", StringComparison.Ordinal) Then
                Return Parse(value)
            End If

            Dim scalar As New KrlValue()
            ParseScalarInto(value, scalar)
            Return scalar.Scalar
        End Function

        Private Shared Sub ParseScalarInto(text As String, result As KrlValue)
            If text.StartsWith("#", StringComparison.Ordinal) Then
                result.Kind = KrlValueKind.EnumValue
                result.Scalar = text.Substring(1)
                Return
            End If

            If String.Equals(text, "TRUE", StringComparison.OrdinalIgnoreCase) Then
                result.Kind = KrlValueKind.Bool
                result.Scalar = True
                Return
            End If

            If String.Equals(text, "FALSE", StringComparison.OrdinalIgnoreCase) Then
                result.Kind = KrlValueKind.Bool
                result.Scalar = False
                Return
            End If

            If text.Length >= 2 AndAlso
               text.StartsWith("""", StringComparison.Ordinal) AndAlso
               text.EndsWith("""", StringComparison.Ordinal) Then
                result.Kind = KrlValueKind.Text
                result.Scalar = text.Substring(1, text.Length - 2)
                Return
            End If

            Dim intValue As Integer
            If Integer.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, intValue) Then
                result.Kind = KrlValueKind.Int
                result.Scalar = intValue
                Return
            End If

            Dim dblValue As Double
            If Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, dblValue) Then
                result.Kind = KrlValueKind.Real
                result.Scalar = dblValue
                Return
            End If

            result.Kind = KrlValueKind.Text
            result.Scalar = text
        End Sub

        ''' <summary>
        ''' Splits on commas that are not inside nested braces or quotes.
        ''' A naive String.Split(","c) breaks on nested structures such as
        ''' {FRAME: X 1, Y 2} appearing as a field value.
        ''' </summary>
        Private Shared Iterator Function SplitTopLevel(body As String) As IEnumerable(Of String)
            Dim depth = 0
            Dim inQuotes = False
            Dim sb As New StringBuilder()

            For Each c In body
                Select Case c
                    Case """"c
                        inQuotes = Not inQuotes
                        sb.Append(c)

                    Case "{"c
                        If Not inQuotes Then depth += 1
                        sb.Append(c)

                    Case "}"c
                        If Not inQuotes Then depth -= 1
                        sb.Append(c)

                    Case ","c
                        If depth = 0 AndAlso Not inQuotes Then
                            Yield sb.ToString()
                            sb.Clear()
                        Else
                            sb.Append(c)
                        End If

                    Case Else
                        sb.Append(c)
                End Select
            Next

            If sb.Length > 0 Then Yield sb.ToString()
        End Function

    End Class

End Namespace
