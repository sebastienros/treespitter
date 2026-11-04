using System.Buffers.Binary;

namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-html: a port of its <c>src/scanner.c</c> and <c>src/tag.h</c> (MIT, Max
/// Brunsfeld). Its state is the stack of the open tags, which gives the implicit end tags and the raw text of the
/// script and style elements.
/// </summary>
public sealed class HtmlScanner : IExternalScanner
{
    // enum TokenType
    private const int StartTagName = 0;
    private const int ScriptStartTagName = 1;
    private const int StyleStartTagName = 2;
    private const int EndTagName = 3;
    private const int ErroneousEndTagName = 4;
    private const int SelfClosingTagDelimiter = 5;
    private const int ImplicitEndTag = 6;
    private const int RawText = 7;
    private const int Comment = 8;

    private static ReadOnlySpan<byte> ScriptEndDelimiter => "</SCRIPT"u8;
    private static ReadOnlySpan<byte> StyleEndDelimiter => "</STYLE"u8;

    // Array(Tag) tags
    private readonly List<Tag> _tags = [];

    // The String of scan_tag_name, reused (the names of the custom tags are copied)
    private readonly List<byte> _tagName = [];

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer)
    {
        var tagCount = _tags.Count > ushort.MaxValue ? ushort.MaxValue : (ushort)_tags.Count;
        ushort serializedTagCount = 0;

        var size = sizeof(ushort);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(size), tagCount);
        size += sizeof(ushort);

        for (; serializedTagCount < tagCount; serializedTagCount++)
        {
            var tag = _tags[serializedTagCount];
            if (tag.Type == TagType.Custom)
            {
                var nameLength = tag.CustomTagName.Length;
                if (nameLength > byte.MaxValue)
                {
                    nameLength = byte.MaxValue;
                }

                if (size + 2 + nameLength >= ExternalScanner.StateCapacity)
                {
                    break;
                }

                buffer[size++] = (byte)tag.Type;
                buffer[size++] = (byte)nameLength;

                // strncpy: the bytes after a NUL are NUL
                var copied = tag.CustomTagName.AsSpan(0, nameLength);
                var nul = copied.IndexOf((byte)0);
                if (nul >= 0)
                {
                    copied.Slice(0, nul).CopyTo(buffer.Slice(size));
                    buffer.Slice(size + nul, nameLength - nul).Clear();
                }
                else
                {
                    copied.CopyTo(buffer.Slice(size));
                }

                size += nameLength;
            }
            else
            {
                if (size + 1 >= ExternalScanner.StateCapacity)
                {
                    break;
                }

                buffer[size++] = (byte)tag.Type;
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(buffer, serializedTagCount);
        return size;
    }

    /// <inheritdoc />
    public void Deserialize(ReadOnlySpan<byte> state)
    {
        _tags.Clear();

        if (state.Length > 0)
        {
            var size = 0;

            var serializedTagCount = BinaryPrimitives.ReadUInt16LittleEndian(state.Slice(size));
            size += sizeof(ushort);

            var tagCount = BinaryPrimitives.ReadUInt16LittleEndian(state.Slice(size));
            size += sizeof(ushort);

            if (tagCount > 0)
            {
                var iter = 0;
                for (; iter < serializedTagCount; iter++)
                {
                    var tag = Tag.New();
                    tag.Type = (TagType)(sbyte)state[size++];
                    if (tag.Type == TagType.Custom)
                    {
                        var nameLength = state[size++];
                        tag.CustomTagName = state.Slice(size, nameLength).ToArray();
                        size += nameLength;
                    }

                    _tags.Add(tag);
                }

                // add zero tags if we didn't read enough, this is because the
                // buffer had no more room but we held more tags.
                for (; iter < tagCount; iter++)
                {
                    _tags.Add(Tag.New());
                }
            }
        }
    }

    private List<byte> ScanTagName(Lexer lexer)
    {
        var tagName = _tagName;
        tagName.Clear();
        while (WideChar.IsAlnum(lexer.Lookahead) || lexer.Lookahead == '-' || lexer.Lookahead == ':')
        {
            // A String is an array of char: the code point is truncated
            tagName.Add((byte)WideChar.ToUpper(lexer.Lookahead));
            lexer.Advance(false);
        }

        return tagName;
    }

    private static bool ScanComment(Lexer lexer)
    {
        if (lexer.Lookahead != '-')
        {
            return false;
        }

        lexer.Advance(false);
        if (lexer.Lookahead != '-')
        {
            return false;
        }

        lexer.Advance(false);

        var dashes = 0U;
        while (lexer.Lookahead != 0)
        {
            switch (lexer.Lookahead)
            {
                case '-':
                    ++dashes;
                    break;
                case '>' when dashes >= 2:
                    lexer.ResultSymbol = Comment;
                    lexer.Advance(false);
                    lexer.MarkEnd();
                    return true;
                default:
                    dashes = 0;
                    break;
            }

            lexer.Advance(false);
        }

        return false;
    }

    private bool ScanRawText(Lexer lexer)
    {
        if (_tags.Count == 0)
        {
            return false;
        }

        lexer.MarkEnd();

        var endDelimiter = _tags[_tags.Count - 1].Type == TagType.Script ? ScriptEndDelimiter : StyleEndDelimiter;

        var delimiterIndex = 0;
        while (lexer.Lookahead != 0)
        {
            if (WideChar.ToUpper(lexer.Lookahead) == endDelimiter[delimiterIndex])
            {
                delimiterIndex++;
                if (delimiterIndex == endDelimiter.Length)
                {
                    break;
                }

                lexer.Advance(false);
            }
            else
            {
                delimiterIndex = 0;
                lexer.Advance(false);
                lexer.MarkEnd();
            }
        }

        lexer.ResultSymbol = RawText;
        return true;
    }

    private void PopTag() => _tags.RemoveAt(_tags.Count - 1);

    private bool ScanImplicitEndTag(Lexer lexer)
    {
        Tag? parent = _tags.Count == 0 ? null : _tags[_tags.Count - 1];

        var isClosingTag = false;
        if (lexer.Lookahead == '/')
        {
            isClosingTag = true;
            lexer.Advance(false);
        }
        else
        {
            if (parent is { } voidParent && voidParent.IsVoid)
            {
                PopTag();
                lexer.ResultSymbol = ImplicitEndTag;
                return true;
            }
        }

        var tagName = ScanTagName(lexer);
        if (tagName.Count == 0 && !lexer.IsAtEnd)
        {
            return false;
        }

        var nextTag = Tag.ForName(tagName);

        if (isClosingTag)
        {
            // The tag correctly closes the topmost element on the stack
            if (_tags.Count > 0 && _tags[_tags.Count - 1].Equals(nextTag))
            {
                return false;
            }

            // Otherwise, dig deeper and queue implicit end tags (to be nice in
            // the case of malformed HTML)
            for (var i = _tags.Count; i > 0; i--)
            {
                if (_tags[i - 1].Type == nextTag.Type)
                {
                    PopTag();
                    lexer.ResultSymbol = ImplicitEndTag;
                    return true;
                }
            }
        }
        else if (
            parent is { } p &&
            (
                !p.CanContain(nextTag) ||
                ((p.Type == TagType.Html || p.Type == TagType.Head || p.Type == TagType.Body) && lexer.IsAtEnd)
            ))
        {
            PopTag();
            lexer.ResultSymbol = ImplicitEndTag;
            return true;
        }

        return false;
    }

    private bool ScanStartTagName(Lexer lexer)
    {
        var tagName = ScanTagName(lexer);
        if (tagName.Count == 0)
        {
            return false;
        }

        var tag = Tag.ForName(tagName);
        _tags.Add(tag);
        switch (tag.Type)
        {
            case TagType.Script:
                lexer.ResultSymbol = ScriptStartTagName;
                break;
            case TagType.Style:
                lexer.ResultSymbol = StyleStartTagName;
                break;
            default:
                lexer.ResultSymbol = StartTagName;
                break;
        }

        return true;
    }

    private bool ScanEndTagName(Lexer lexer)
    {
        var tagName = ScanTagName(lexer);

        if (tagName.Count == 0)
        {
            return false;
        }

        var tag = Tag.ForName(tagName);
        if (_tags.Count > 0 && _tags[_tags.Count - 1].Equals(tag))
        {
            PopTag();
            lexer.ResultSymbol = EndTagName;
        }
        else
        {
            lexer.ResultSymbol = ErroneousEndTagName;
        }

        return true;
    }

    private bool ScanSelfClosingTagDelimiter(Lexer lexer)
    {
        lexer.Advance(false);
        if (lexer.Lookahead == '>')
        {
            lexer.Advance(false);
            if (_tags.Count > 0)
            {
                PopTag();
                lexer.ResultSymbol = SelfClosingTagDelimiter;
            }

            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[RawText] && !validSymbols[StartTagName] && !validSymbols[EndTagName])
        {
            return ScanRawText(lexer);
        }

        while (WideChar.IsSpace(lexer.Lookahead))
        {
            lexer.Advance(true);
        }

        switch (lexer.Lookahead)
        {
            case '<':
                lexer.MarkEnd();
                lexer.Advance(false);

                if (lexer.Lookahead == '!')
                {
                    lexer.Advance(false);
                    return ScanComment(lexer);
                }

                if (validSymbols[ImplicitEndTag])
                {
                    return ScanImplicitEndTag(lexer);
                }

                break;

            case '\0':
                if (validSymbols[ImplicitEndTag])
                {
                    return ScanImplicitEndTag(lexer);
                }

                break;

            case '/':
                if (validSymbols[SelfClosingTagDelimiter])
                {
                    return ScanSelfClosingTagDelimiter(lexer);
                }

                break;

            default:
                if ((validSymbols[StartTagName] || validSymbols[EndTagName]) && !validSymbols[RawText])
                {
                    return validSymbols[StartTagName] ? ScanStartTagName(lexer) : ScanEndTagName(lexer);
                }

                break;
        }

        return false;
    }

    // tag.h

    private enum TagType
    {
        Area,
        Base,
        Basefont,
        Bgsound,
        Br,
        Col,
        Command,
        Embed,
        Frame,
        Hr,
        Image,
        Img,
        Input,
        Isindex,
        Keygen,
        Link,
        Menuitem,
        Meta,
        Nextid,
        Param,
        Source,
        Track,
        Wbr,
        EndOfVoidTags,

        A,
        Abbr,
        Address,
        Article,
        Aside,
        Audio,
        B,
        Bdi,
        Bdo,
        Blockquote,
        Body,
        Button,
        Canvas,
        Caption,
        Cite,
        Code,
        Colgroup,
        Data,
        Datalist,
        Dd,
        Del,
        Details,
        Dfn,
        Dialog,
        Div,
        Dl,
        Dt,
        Em,
        Fieldset,
        Figcaption,
        Figure,
        Footer,
        Form,
        H1,
        H2,
        H3,
        H4,
        H5,
        H6,
        Head,
        Header,
        Hgroup,
        Html,
        I,
        Iframe,
        Ins,
        Kbd,
        Label,
        Legend,
        Li,
        Main,
        Map,
        Mark,
        Math,
        Menu,
        Meter,
        Nav,
        Noscript,
        Object,
        Ol,
        Optgroup,
        Option,
        Output,
        P,
        Picture,
        Pre,
        Progress,
        Q,
        Rb,
        Rp,
        Rt,
        Rtc,
        Ruby,
        S,
        Samp,
        Script,
        Section,
        Select,
        Slot,
        Small,
        Span,
        Strong,
        Style,
        Sub,
        Summary,
        Sup,
        Svg,
        Table,
        Tbody,
        Td,
        Template,
        Textarea,
        Tfoot,
        Th,
        Thead,
        Time,
        Title,
        Tr,
        U,
        Ul,
        Var,
        Video,

        Custom,

        End,
    }

    // TAG_TYPES_BY_TAG_NAME
    private static readonly (byte[] TagName, TagType TagType)[] TagTypesByTagName = TagMap(
    [
        ("AREA", TagType.Area),
        ("BASE", TagType.Base),
        ("BASEFONT", TagType.Basefont),
        ("BGSOUND", TagType.Bgsound),
        ("BR", TagType.Br),
        ("COL", TagType.Col),
        ("COMMAND", TagType.Command),
        ("EMBED", TagType.Embed),
        ("FRAME", TagType.Frame),
        ("HR", TagType.Hr),
        ("IMAGE", TagType.Image),
        ("IMG", TagType.Img),
        ("INPUT", TagType.Input),
        ("ISINDEX", TagType.Isindex),
        ("KEYGEN", TagType.Keygen),
        ("LINK", TagType.Link),
        ("MENUITEM", TagType.Menuitem),
        ("META", TagType.Meta),
        ("NEXTID", TagType.Nextid),
        ("PARAM", TagType.Param),
        ("SOURCE", TagType.Source),
        ("TRACK", TagType.Track),
        ("WBR", TagType.Wbr),
        ("A", TagType.A),
        ("ABBR", TagType.Abbr),
        ("ADDRESS", TagType.Address),
        ("ARTICLE", TagType.Article),
        ("ASIDE", TagType.Aside),
        ("AUDIO", TagType.Audio),
        ("B", TagType.B),
        ("BDI", TagType.Bdi),
        ("BDO", TagType.Bdo),
        ("BLOCKQUOTE", TagType.Blockquote),
        ("BODY", TagType.Body),
        ("BUTTON", TagType.Button),
        ("CANVAS", TagType.Canvas),
        ("CAPTION", TagType.Caption),
        ("CITE", TagType.Cite),
        ("CODE", TagType.Code),
        ("COLGROUP", TagType.Colgroup),
        ("DATA", TagType.Data),
        ("DATALIST", TagType.Datalist),
        ("DD", TagType.Dd),
        ("DEL", TagType.Del),
        ("DETAILS", TagType.Details),
        ("DFN", TagType.Dfn),
        ("DIALOG", TagType.Dialog),
        ("DIV", TagType.Div),
        ("DL", TagType.Dl),
        ("DT", TagType.Dt),
        ("EM", TagType.Em),
        ("FIELDSET", TagType.Fieldset),
        ("FIGCAPTION", TagType.Figcaption),
        ("FIGURE", TagType.Figure),
        ("FOOTER", TagType.Footer),
        ("FORM", TagType.Form),
        ("H1", TagType.H1),
        ("H2", TagType.H2),
        ("H3", TagType.H3),
        ("H4", TagType.H4),
        ("H5", TagType.H5),
        ("H6", TagType.H6),
        ("HEAD", TagType.Head),
        ("HEADER", TagType.Header),
        ("HGROUP", TagType.Hgroup),
        ("HTML", TagType.Html),
        ("I", TagType.I),
        ("IFRAME", TagType.Iframe),
        ("INS", TagType.Ins),
        ("KBD", TagType.Kbd),
        ("LABEL", TagType.Label),
        ("LEGEND", TagType.Legend),
        ("LI", TagType.Li),
        ("MAIN", TagType.Main),
        ("MAP", TagType.Map),
        ("MARK", TagType.Mark),
        ("MATH", TagType.Math),
        ("MENU", TagType.Menu),
        ("METER", TagType.Meter),
        ("NAV", TagType.Nav),
        ("NOSCRIPT", TagType.Noscript),
        ("OBJECT", TagType.Object),
        ("OL", TagType.Ol),
        ("OPTGROUP", TagType.Optgroup),
        ("OPTION", TagType.Option),
        ("OUTPUT", TagType.Output),
        ("P", TagType.P),
        ("PICTURE", TagType.Picture),
        ("PRE", TagType.Pre),
        ("PROGRESS", TagType.Progress),
        ("Q", TagType.Q),
        ("RB", TagType.Rb),
        ("RP", TagType.Rp),
        ("RT", TagType.Rt),
        ("RTC", TagType.Rtc),
        ("RUBY", TagType.Ruby),
        ("S", TagType.S),
        ("SAMP", TagType.Samp),
        ("SCRIPT", TagType.Script),
        ("SECTION", TagType.Section),
        ("SELECT", TagType.Select),
        ("SLOT", TagType.Slot),
        ("SMALL", TagType.Small),
        ("SPAN", TagType.Span),
        ("STRONG", TagType.Strong),
        ("STYLE", TagType.Style),
        ("SUB", TagType.Sub),
        ("SUMMARY", TagType.Summary),
        ("SUP", TagType.Sup),
        ("SVG", TagType.Svg),
        ("TABLE", TagType.Table),
        ("TBODY", TagType.Tbody),
        ("TD", TagType.Td),
        ("TEMPLATE", TagType.Template),
        ("TEXTAREA", TagType.Textarea),
        ("TFOOT", TagType.Tfoot),
        ("TH", TagType.Th),
        ("THEAD", TagType.Thead),
        ("TIME", TagType.Time),
        ("TITLE", TagType.Title),
        ("TR", TagType.Tr),
        ("U", TagType.U),
        ("UL", TagType.Ul),
        ("VAR", TagType.Var),
        ("VIDEO", TagType.Video),
        ("CUSTOM", TagType.Custom),
    ]);

    private static readonly TagType[] TagTypesNotAllowedInParagraphs =
    [
        TagType.Address, TagType.Article, TagType.Aside, TagType.Blockquote, TagType.Details, TagType.Div, TagType.Dl,
        TagType.Fieldset, TagType.Figcaption, TagType.Figure, TagType.Footer, TagType.Form, TagType.H1, TagType.H2,
        TagType.H3, TagType.H4, TagType.H5, TagType.H6, TagType.Header, TagType.Hr, TagType.Main,
        TagType.Nav, TagType.Ol, TagType.P, TagType.Pre, TagType.Section,
    ];

    private static (byte[] TagName, TagType TagType)[] TagMap((string TagName, TagType TagType)[] entries)
    {
        var map = new (byte[] TagName, TagType TagType)[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            var name = new byte[entries[i].TagName.Length];
            for (var j = 0; j < name.Length; j++)
            {
                name[j] = (byte)entries[i].TagName[j];
            }

            map[i] = (name, entries[i].TagType);
        }

        return map;
    }

    private static TagType TagTypeForName(List<byte> tagName)
    {
        for (var i = 0; i < TagTypesByTagName.Length; i++)
        {
            var entry = TagTypesByTagName[i];
            if (entry.TagName.Length == tagName.Count && Equal(entry.TagName, tagName))
            {
                return entry.TagType;
            }
        }

        return TagType.Custom;
    }

    private static bool Equal(ReadOnlySpan<byte> name, List<byte> other)
    {
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] != other[i])
            {
                return false;
            }
        }

        return true;
    }

    private struct Tag
    {
        public TagType Type;

        public byte[] CustomTagName;

        public static Tag New() => new() { Type = TagType.End, CustomTagName = [] };

        public static Tag ForName(List<byte> name)
        {
            var tag = New();
            tag.Type = TagTypeForName(name);
            if (tag.Type == TagType.Custom)
            {
                tag.CustomTagName = [.. name];
            }

            return tag;
        }

        public readonly bool IsVoid => Type < TagType.EndOfVoidTags;

        public readonly bool Equals(Tag other)
        {
            if (Type != other.Type)
            {
                return false;
            }

            if (Type == TagType.Custom)
            {
                if (CustomTagName.Length != other.CustomTagName.Length)
                {
                    return false;
                }

                if (!CustomTagName.AsSpan().SequenceEqual(other.CustomTagName))
                {
                    return false;
                }
            }

            return true;
        }

        public readonly bool CanContain(Tag other)
        {
            var child = other.Type;

            switch (Type)
            {
                case TagType.Li:
                    return child != TagType.Li;

                case TagType.Dt:
                case TagType.Dd:
                    return child != TagType.Dt && child != TagType.Dd;

                case TagType.P:
                    for (var i = 0; i < 26; i++)
                    {
                        if (child == TagTypesNotAllowedInParagraphs[i])
                        {
                            return false;
                        }
                    }

                    return true;

                case TagType.Colgroup:
                    return child == TagType.Col;

                case TagType.Rb:
                case TagType.Rt:
                case TagType.Rp:
                    return child != TagType.Rb && child != TagType.Rt && child != TagType.Rp;

                case TagType.Optgroup:
                    return child != TagType.Optgroup;

                case TagType.Tr:
                    return child != TagType.Tr;

                case TagType.Td:
                case TagType.Th:
                    return child != TagType.Td && child != TagType.Th && child != TagType.Tr;

                default:
                    return true;
            }
        }
    }
}
