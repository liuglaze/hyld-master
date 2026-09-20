using System;
using System.Collections.Generic;
using System.Text;

namespace PMNetGen
{
    /// <summary>
    /// 极简 proto3 解析器。
    ///
    /// 只覆盖本项目权威 proto（SocketProto.proto）实际用到的语法子集：
    /// syntax / package / enum / message / 普通字段 / repeated 字段 / 行内与块注释。
    /// 其余语句（option、import、reserved、嵌套定义）会被安全跳过，不报错。
    ///
    /// 之所以不依赖 protoc 或 Google.Protobuf 的描述符：
    /// 本次改造的目标（决策 D1）正是去掉运行时反射描述符依赖，生成器自身也不应引入该依赖。
    /// </summary>
    internal sealed class ProtoParser
    {
        private readonly List<Token> _tokens;
        private int _index;

        private ProtoParser(List<Token> tokens)
        {
            _tokens = tokens;
            _index = 0;
        }

        public static ProtoFile Parse(string text, string sourceName)
        {
            List<Token> tokens = Lex(text, sourceName);
            ProtoParser parser = new ProtoParser(tokens);
            return parser.ParseFile();
        }

        // ---------------- 词法 ----------------

        private enum TokenKind
        {
            Eof,
            Ident,
            Number,
            String,
            Symbol,
        }

        private struct Token
        {
            public TokenKind Kind;
            public string Text;
            public int Line;
        }

        private static List<Token> Lex(string text, string sourceName)
        {
            List<Token> tokens = new List<Token>();
            int i = 0;
            int line = 1;
            int length = text.Length;

            while (i < length)
            {
                char c = text[i];

                if (c == '\n')
                {
                    line++;
                    i++;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                // 行注释
                if (c == '/' && i + 1 < length && text[i + 1] == '/')
                {
                    while (i < length && text[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                // 块注释
                if (c == '/' && i + 1 < length && text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < length && !(text[i] == '*' && text[i + 1] == '/'))
                    {
                        if (text[i] == '\n')
                        {
                            line++;
                        }

                        i++;
                    }

                    i += 2;
                    continue;
                }

                // 字符串
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    int start = ++i;
                    while (i < length && text[i] != quote)
                    {
                        if (text[i] == '\\')
                        {
                            i++;
                        }

                        i++;
                    }

                    Token strTok = new Token();
                    strTok.Kind = TokenKind.String;
                    strTok.Text = text.Substring(start, i - start);
                    strTok.Line = line;
                    tokens.Add(strTok);
                    i++;
                    continue;
                }

                // 标识符 / 关键字
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    {
                        i++;
                    }

                    Token idTok = new Token();
                    idTok.Kind = TokenKind.Ident;
                    idTok.Text = text.Substring(start, i - start);
                    idTok.Line = line;
                    tokens.Add(idTok);
                    continue;
                }

                // 数字
                if (char.IsDigit(c))
                {
                    int start = i;
                    while (i < length && (char.IsLetterOrDigit(text[i]) || text[i] == '.'))
                    {
                        i++;
                    }

                    Token numTok = new Token();
                    numTok.Kind = TokenKind.Number;
                    numTok.Text = text.Substring(start, i - start);
                    numTok.Line = line;
                    tokens.Add(numTok);
                    continue;
                }

                // 符号
                if ("{}[]=;,.<>()".IndexOf(c) >= 0)
                {
                    Token symTok = new Token();
                    symTok.Kind = TokenKind.Symbol;
                    symTok.Text = c.ToString();
                    symTok.Line = line;
                    tokens.Add(symTok);
                    i++;
                    continue;
                }

                // 其他字符（例如 '-'）单独作为符号交给上层处理
                Token otherTok = new Token();
                otherTok.Kind = TokenKind.Symbol;
                otherTok.Text = c.ToString();
                otherTok.Line = line;
                tokens.Add(otherTok);
                i++;
            }

            Token eof = new Token();
            eof.Kind = TokenKind.Eof;
            eof.Text = string.Empty;
            eof.Line = line;
            tokens.Add(eof);

            if (text.Length == 0)
            {
                throw new ProtoParseException(sourceName, 1, "proto 文件为空");
            }

            return tokens;
        }

        // ---------------- 语法 ----------------

        private ProtoFile ParseFile()
        {
            ProtoFile file = new ProtoFile();

            while (!AtEnd)
            {
                Token t = Peek();

                if (IsIdent(t, "syntax"))
                {
                    Advance();
                    ExpectSymbol("=");
                    file.Syntax = ParseStringLiteral();
                    ExpectSymbol(";");
                    continue;
                }

                if (IsIdent(t, "package"))
                {
                    Advance();
                    file.Package = ParseDottedName();
                    ExpectSymbol(";");
                    continue;
                }

                if (IsIdent(t, "enum"))
                {
                    file.Enums.Add(ParseEnum());
                    continue;
                }

                if (IsIdent(t, "message"))
                {
                    file.Messages.Add(ParseMessage(file));
                    continue;
                }

                // option / import / service / extend / 未知语句：整条跳过
                SkipStatement();
            }

            Resolve(file);
            return file;
        }

        private ProtoEnum ParseEnum()
        {
            ExpectIdent("enum");
            ProtoEnum result = new ProtoEnum();
            result.Name = ExpectIdentifier();
            ExpectSymbol("{");

            while (!AtEnd && !IsSymbol(Peek(), "}"))
            {
                Token t = Peek();

                if (IsIdent(t, "option") || IsIdent(t, "reserved"))
                {
                    SkipStatement();
                    continue;
                }

                if (t.Kind == TokenKind.Symbol && t.Text == ";")
                {
                    Advance();
                    continue;
                }

                ProtoEnumValue value = new ProtoEnumValue();
                value.Name = ExpectIdentifier();

                if (IsSymbol(Peek(), "="))
                {
                    Advance();
                    value.Number = ParseSignedInt();
                    SkipUntilSymbol(";");
                }

                result.Values.Add(value);
            }

            ExpectSymbol("}");
            return result;
        }

        private ProtoMessage ParseMessage(ProtoFile file)
        {
            ExpectIdent("message");
            ProtoMessage result = new ProtoMessage();
            result.Name = ExpectIdentifier();
            ExpectSymbol("{");

            while (!AtEnd && !IsSymbol(Peek(), "}"))
            {
                Token t = Peek();

                if (IsIdent(t, "option") || IsIdent(t, "reserved"))
                {
                    SkipStatement();
                    continue;
                }

                // 嵌套 message / enum：跳过整个块（本项目权威 proto 未使用，仅保证不崩）
                if (IsIdent(t, "message") || IsIdent(t, "enum"))
                {
                    SkipBlock();
                    continue;
                }

                if (t.Kind == TokenKind.Symbol && t.Text == ";")
                {
                    Advance();
                    continue;
                }

                result.Fields.Add(ParseField());
            }

            ExpectSymbol("}");
            return result;
        }

        private ProtoField ParseField()
        {
            ProtoField field = new ProtoField();

            if (IsIdent(Peek(), "repeated"))
            {
                Advance();
                field.Repeated = true;
            }
            else if (IsIdent(Peek(), "optional"))
            {
                // proto3 的显式 optional：本生成器暂不生成 Has 语义，按普通字段处理
                Advance();
            }

            field.TypeName = ParseDottedName();
            field.Name = ExpectIdentifier();
            ExpectSymbol("=");
            field.Number = ParseSignedInt();

            // 字段选项：[packed = false, deprecated = true, ...]
            if (IsSymbol(Peek(), "["))
            {
                Advance();
                int depth = 1;
                while (!AtEnd && depth > 0)
                {
                    Token t = Peek();
                    if (IsSymbol(t, "["))
                    {
                        depth++;
                    }
                    else if (IsSymbol(t, "]"))
                    {
                        depth--;
                        if (depth == 0)
                        {
                            Advance();
                            break;
                        }
                    }
                    else if (IsIdent(t, "packed") && IsSymbol(PeekAt(1), "="))
                    {
                        Advance();
                        Advance();
                        Token v = Peek();
                        if (v.Kind == TokenKind.Ident && v.Text == "false")
                        {
                            field.Packed = false;
                        }

                        Advance();
                        continue;
                    }

                    Advance();
                }
            }

            ExpectSymbol(";");
            return field;
        }

        /// <summary>解析结束后解析类型引用，填充 Kind / Scalar / C# 类型名。</summary>
        private static void Resolve(ProtoFile file)
        {
            for (int i = 0; i < file.Messages.Count; i++)
            {
                ProtoMessage msg = file.Messages[i];
                for (int j = 0; j < msg.Fields.Count; j++)
                {
                    ProtoField field = msg.Fields[j];

                    ProtoScalar scalar = ProtoFile.ParseScalarKeyword(field.TypeName);
                    if (scalar != ProtoScalar.None)
                    {
                        field.Kind = ProtoFieldKind.Scalar;
                        field.Scalar = scalar;
                        field.CsType = ScalarToCsType(scalar);
                    }
                    else if (file.FindEnum(field.TypeName) != null)
                    {
                        field.Kind = ProtoFieldKind.Enum;
                        field.CsType = "PM" + field.TypeName;
                    }
                    else if (file.FindMessage(field.TypeName) != null)
                    {
                        field.Kind = ProtoFieldKind.Message;
                        field.CsType = "PM" + field.TypeName;
                    }
                    else
                    {
                        throw new ProtoParseException("(resolve)", 0,
                            "字段 " + msg.Name + "." + field.Name + " 的类型无法解析: " + field.TypeName);
                    }

                    // proto3：可 pack 的 repeated 默认 packed
                    field.Packed = field.Repeated && field.IsPackable;

                    field.CsName = Naming.ToPascalCase(field.Name);
                }
            }
        }

        private static string ScalarToCsType(ProtoScalar scalar)
        {
            switch (scalar)
            {
                case ProtoScalar.Double: return "double";
                case ProtoScalar.Float: return "float";
                case ProtoScalar.Int32: return "int";
                case ProtoScalar.Int64: return "long";
                case ProtoScalar.UInt32: return "uint";
                case ProtoScalar.UInt64: return "ulong";
                case ProtoScalar.SInt32: return "int";
                case ProtoScalar.SInt64: return "long";
                case ProtoScalar.Fixed32: return "uint";
                case ProtoScalar.Fixed64: return "ulong";
                case ProtoScalar.SFixed32: return "int";
                case ProtoScalar.SFixed64: return "long";
                case ProtoScalar.Bool: return "bool";
                case ProtoScalar.String: return "string";
                case ProtoScalar.Bytes: return "byte[]";
                default: return "object";
            }
        }

        // ---------------- 词法辅助 ----------------

        private bool AtEnd
        {
            get { return _tokens[_index].Kind == TokenKind.Eof; }
        }

        private Token Peek()
        {
            return _tokens[_index];
        }

        private Token PeekAt(int offset)
        {
            int target = _index + offset;
            if (target >= _tokens.Count)
            {
                return _tokens[_tokens.Count - 1];
            }

            return _tokens[target];
        }

        private void Advance()
        {
            if (_index < _tokens.Count - 1)
            {
                _index++;
            }
        }

        private static bool IsIdent(Token t, string text)
        {
            return t.Kind == TokenKind.Ident && t.Text == text;
        }

        private static bool IsSymbol(Token t, string text)
        {
            return t.Kind == TokenKind.Symbol && t.Text == text;
        }

        private void ExpectIdent(string text)
        {
            if (!IsIdent(Peek(), text))
            {
                throw Error("期望关键字 " + text + "，实际是 " + Describe(Peek()));
            }

            Advance();
        }

        private string ExpectIdentifier()
        {
            Token t = Peek();
            if (t.Kind != TokenKind.Ident)
            {
                throw Error("期望标识符，实际是 " + Describe(t));
            }

            Advance();
            return t.Text;
        }

        private void ExpectSymbol(string symbol)
        {
            if (!IsSymbol(Peek(), symbol))
            {
                throw Error("期望符号 " + symbol + "，实际是 " + Describe(Peek()));
            }

            Advance();
        }

        private string ParseDottedName()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(ExpectIdentifier());
            while (IsSymbol(Peek(), "."))
            {
                Advance();
                sb.Append('.');
                sb.Append(ExpectIdentifier());
            }

            return sb.ToString();
        }

        private string ParseStringLiteral()
        {
            Token t = Peek();
            if (t.Kind != TokenKind.String)
            {
                throw Error("期望字符串字面量，实际是 " + Describe(t));
            }

            Advance();
            return t.Text;
        }

        private int ParseSignedInt()
        {
            bool negative = false;
            if (IsSymbol(Peek(), "-"))
            {
                negative = true;
                Advance();
            }

            Token t = Peek();
            if (t.Kind != TokenKind.Number)
            {
                throw Error("期望整数，实际是 " + Describe(t));
            }

            Advance();
            int value;
            if (!int.TryParse(t.Text, out value))
            {
                throw Error("整数无法解析: " + t.Text);
            }

            return negative ? -value : value;
        }

        private void SkipUntilSymbol(string symbol)
        {
            while (!AtEnd && !IsSymbol(Peek(), symbol))
            {
                Advance();
            }

            if (IsSymbol(Peek(), symbol))
            {
                Advance();
            }
        }

        /// <summary>跳过一条以 ; 结束的语句。</summary>
        private void SkipStatement()
        {
            int depth = 0;
            while (!AtEnd)
            {
                Token t = Peek();
                if (IsSymbol(t, "{"))
                {
                    depth++;
                }
                else if (IsSymbol(t, "}"))
                {
                    if (depth == 0)
                    {
                        return;
                    }

                    depth--;
                }
                else if (IsSymbol(t, ";") && depth == 0)
                {
                    Advance();
                    return;
                }

                Advance();
            }
        }

        /// <summary>跳过一整个 { ... } 块（含前面的关键字）。</summary>
        private void SkipBlock()
        {
            // 先走到 {
            while (!AtEnd && !IsSymbol(Peek(), "{"))
            {
                if (IsSymbol(Peek(), ";"))
                {
                    Advance();
                    return;
                }

                Advance();
            }

            if (!IsSymbol(Peek(), "{"))
            {
                return;
            }

            int depth = 0;
            while (!AtEnd)
            {
                Token t = Peek();
                if (IsSymbol(t, "{"))
                {
                    depth++;
                }
                else if (IsSymbol(t, "}"))
                {
                    depth--;
                    if (depth == 0)
                    {
                        Advance();
                        return;
                    }
                }

                Advance();
            }
        }

        private ProtoParseException Error(string message)
        {
            return new ProtoParseException("(parse)", Peek().Line, message);
        }

        private static string Describe(Token t)
        {
            return t.Kind == TokenKind.Eof ? "<EOF>" : (t.Kind + " '" + t.Text + "'");
        }
    }

    internal sealed class ProtoParseException : Exception
    {
        public ProtoParseException(string source, int line, string message)
            : base(source + "(" + line + "): " + message)
        {
        }
    }

    /// <summary>命名转换：与 protoc 的 C# 命名风格保持一致。</summary>
    internal static class Naming
    {
        /// <summary>
        /// proto 字段名 → C# 成员名。
        /// 规则：下划线分词，每段首字母大写，其余字符原样保留。
        /// 例：battle_net_sim_config → BattleNetSimConfig；battleInfo → BattleInfo；pos_x → PosX。
        /// </summary>
        public static string ToPascalCase(string protoName)
        {
            if (string.IsNullOrEmpty(protoName))
            {
                return protoName;
            }

            StringBuilder sb = new StringBuilder(protoName.Length);
            bool upperNext = true;
            for (int i = 0; i < protoName.Length; i++)
            {
                char c = protoName[i];
                if (c == '_')
                {
                    upperNext = true;
                    continue;
                }

                if (upperNext)
                {
                    sb.Append(char.ToUpperInvariant(c));
                    upperNext = false;
                }
                else
                {
                    sb.Append(c);
                }
            }

            return EscapeIfKeyword(sb.ToString());
        }

        /// <summary>若标识符是 C# 关键字则加 @ 前缀。</summary>
        public static string EscapeIfKeyword(string name)
        {
            for (int i = 0; i < CSharpKeywords.Length; i++)
            {
                if (CSharpKeywords[i] == name)
                {
                    return "@" + name;
                }
            }

            return name;
        }

        private static readonly string[] CSharpKeywords = new string[]
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
            "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
            "void", "volatile", "while",
        };
    }
}
