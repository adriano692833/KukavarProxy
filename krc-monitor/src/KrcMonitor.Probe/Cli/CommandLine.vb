Imports System.Globalization

''' <summary>
''' Minimal argument parser. Deliberately dependency-free: the probe has to be
''' copied onto a controller as a single self-contained executable, and pulling
''' in a NuGet CLI package would mean shipping extra assemblies onto a machine
''' where every added file has to be justified.
'''
''' Grammar:
'''   &lt;command&gt; [positional ...] [--option value] [--flag]
''' </summary>
Public NotInheritable Class CommandLine

    Private ReadOnly _options As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _flags As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _positionals As New List(Of String)

    ''' <summary>Options that take a value; anything else starting with -- is a flag.</summary>
    Private Shared ReadOnly ValuedOptions As String() = {
        "client", "timeout", "typelib", "filter", "interval", "duration", "count"
    }

    Public Property Command As String

    Public ReadOnly Property Positionals As IReadOnlyList(Of String)
        Get
            Return _positionals
        End Get
    End Property

    Private Sub New()
    End Sub

    Public Shared Function Parse(args As String()) As CommandLine
        Dim cli As New CommandLine()

        If args Is Nothing OrElse args.Length = 0 Then
            Return cli
        End If

        Dim index = 0

        If Not args(0).StartsWith("-", StringComparison.Ordinal) Then
            cli.Command = args(0).Trim().ToLowerInvariant()
            index = 1
        Else
            cli.Command = "help"
        End If

        While index < args.Length
            Dim arg = args(index)

            If arg.StartsWith("--", StringComparison.Ordinal) Then
                Dim name = arg.Substring(2)
                If name.Length = 0 Then
                    Throw New ArgumentException("Empty option name ('--').")
                End If

                ' Support --name=value as well as --name value.
                Dim eq = name.IndexOf("="c)
                If eq > 0 Then
                    cli._options(name.Substring(0, eq)) = name.Substring(eq + 1)
                    index += 1
                    Continue While
                End If

                If IsValued(name) Then
                    If index + 1 >= args.Length Then
                        Throw New ArgumentException($"Option '--{name}' requires a value.")
                    End If
                    cli._options(name) = args(index + 1)
                    index += 2
                Else
                    cli._flags.Add(name)
                    index += 1
                End If

            ElseIf arg.StartsWith("-", StringComparison.Ordinal) AndAlso arg.Length > 1 AndAlso
                   Not Char.IsDigit(arg(1)) Then
                ' A single dash is reserved. KRL variable names begin with '$',
                ' never '-', so this cannot swallow a legitimate positional.
                Throw New ArgumentException($"Unrecognised argument '{arg}'. Options use a double dash.")

            Else
                cli._positionals.Add(arg)
                index += 1
            End If
        End While

        Return cli
    End Function

    Private Shared Function IsValued(name As String) As Boolean
        For Each v In ValuedOptions
            If String.Equals(v, name, StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

    Public Function GetOption(name As String) As String
        Dim value As String = Nothing
        Return If(_options.TryGetValue(name, value), value, Nothing)
    End Function

    Public Function GetOptionInt(name As String, defaultValue As Integer) As Integer
        Dim raw = GetOption(name)
        If raw Is Nothing Then Return defaultValue

        Dim parsed As Integer
        If Not Integer.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, parsed) Then
            Throw New ArgumentException($"Option '--{name}' expects an integer, got '{raw}'.")
        End If

        Return parsed
    End Function

    Public Function HasFlag(name As String) As Boolean
        Return _flags.Contains(name)
    End Function

End Class
