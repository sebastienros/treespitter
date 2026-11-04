namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-haskell: a port of its <c>src/scanner.c</c> and <c>src/unicode.h</c> (MIT, Max
/// Brunsfeld, Rick Winfrey, Owen Shepherd and Torsten Schmits). Its state is the stack of layout contexts and the state
/// of the newline processing; the lookahead buffer is reset by each deserialization. The debugging code of the C scanner
/// (<c>TREE_SITTER_DEBUG</c>) isn't ported.
/// </summary>
/// <remarks>
/// The C functions take the transient state, <c>Env</c>, as a parameter: it's in the fields of the scanner here (the
/// lexer, the valid symbols and <c>symop</c>), set by each scan. <c>SEQ(expr)</c> (return the result of the expression
/// unless it's <c>FAIL</c>) is an <c>if</c> that returns the result.
/// </remarks>
public sealed partial class HaskellScanner : IExternalScanner
{
    // --------------------------------------------------------------------------------------------------------
    // Symbols
    // --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// This enum mirrors the symbols in <c>externals</c> in <c>grammar/externals.js</c>.
    /// tree-sitter passes an array of booleans to the scanner whose entries are <c>true</c> if the symbol at the
    /// corresponding index is valid at the current parser position.
    /// </summary>
    private enum Symbol
    {
        Fail,
        Semicolon,
        Start,
        StartDo,
        StartCase,
        StartIf,
        StartLet,
        StartQuote,
        StartExplicit,
        End,
        EndExplicit,
        StartBrace,
        EndBrace,
        StartTexp,
        EndTexp,
        Where,
        In,
        Arrow,
        Bar,
        Deriving,
        Comment,
        Haddock,
        Cpp,
        Pragma,
        QqStart,
        QqBody,
        Splice,
        QualDot,
        TightDot,
        PrefixDot,
        Dotdot,
        TightAt,
        PrefixAt,
        TightBang,
        PrefixBang,
        TightTilde,
        PrefixTilde,
        PrefixPercent,
        QualifiedOp,
        LeftSectionOp,
        NoSectionOp,
        Minus,
        Context,
        Infix,
        DataInfix,
        TypeInstance,
        Varsym,
        Consym,
        Update,
    }

    // --------------------------------------------------------------------------------------------------------
    // Data
    // --------------------------------------------------------------------------------------------------------

    /// <summary>Different sorts of layout contexts that require special treatment.</summary>
    private enum ContextSort
    {
        DeclLayout,
        DoLayout,
        CaseLayout,
        LetLayout,
        QuoteLayout,
        MultiWayIfLayout,
        Braces,
        TExp,
        ModuleHeader,
        NoContext,
    }

    /// <summary>
    /// The persistent state maintains a stack of layout contexts.
    /// New entries are created when a layout symbol is valid at the current position, and they are removed when the
    /// indent of a line satisfies conditions that depend on the current context sort, or when certain tokens (like
    /// <c>else</c>) occur.
    /// </summary>
    private struct Context
    {
        public ContextSort Sort;
        public uint Indent;
    }

    /// <summary>This enumerates the lookahead tokens that have special meaning in the scanner.</summary>
    private enum Lexed
    {
        LNothing,
        LEof,
        LWhere,
        LIn,
        LThen,
        LElse,
        LDeriving,
        LModule,
        LUpper,
        LTick,
        LSymop,
        LSymopSpecial,
        LDotDot,
        LDotId,
        LDotSymop,
        LDotOpen,
        LDollar,
        LBang,
        LTilde,
        LAt,
        LPercent,
        LHash,
        LBar,
        LArrow,
        LCArrow,
        LTexpCloser,
        LQuoteClose,
        LPragma,
        LBlockComment,
        LLineComment,
        LBraceClose,
        LBraceOpen,
        LBracketOpen,
        LUnboxedClose,
        LSemi,
        LCppElse,
        LCpp,
    }

    /// <summary>
    /// The current newline mode.
    /// <c>NInit</c> is set during newline lookahead, and <c>NProcess</c> when lookahead has finished.
    /// After processing is complete, the state is reset to <c>NInactive</c>.
    /// <c>NResume</c> is a special variant that forces newline lookahead mode when a run starts without requiring a
    /// newline.
    /// This is used for the beginning of the file and after pragmas (see <c>pragma</c>).
    /// </summary>
    private enum NewlineState
    {
        NInactive,
        NInit,
        NProcess,
        NResume,
    }

    /// <summary>
    /// The two newline modes need to operate across multiple scanner runs and adapt their behavior to the context
    /// established by previous runs, encoded by this persistent state.
    /// </summary>
    private struct Newline
    {
        public NewlineState State;

        // The final token encountered after skipping comments and CPP.
        public Lexed End;

        // The indent of `end`, used to decide layout actions before parsing intermediate extras.
        public uint Indent;

        // When there is no token after extras, we shouldn't start layouts.
        public bool Eof;

        // Prohibit layout semicolons in future runs.
        public bool NoSemi;

        // Prohibit layout semicolons in future runs, but can be relaxed by some actions.
        // See `explicit_semicolon`.
        public bool SkipSemi;

        // Lookahead has advanced into `end`, so the scanner has to be restarted before processing the newline result.
        public bool Unsafe;
    }

    // The state that is persisted across scanner runs (State): the layout context stack (Contexts), the newline state
    // and the lookahead buffer (Lookahead), whose size and offset are reset by each deserialization.
    private Context[] _contexts = new Context[8];
    private uint _contextsSize;
    private Newline _newline;
    private int[] _lookaheadContents = new int[8];
    private uint _lookaheadSize;
    private uint _lookaheadOffset;

    // The transient state (Env)
    private Lexer _lexer = null!;
    private readonly bool[] _symbols = new bool[(int)Symbol.Update + 1];
    private uint _symop;

    private void ResetNewline() => _newline = default;

    private bool NewlineActive() => _newline.State == NewlineState.NInit || _newline.State == NewlineState.NProcess;

    private bool NewlineInit() => _newline.State == NewlineState.NInit;

    // --------------------------------------------------------------------------------------------------------
    // Lexer interaction
    // --------------------------------------------------------------------------------------------------------

    private bool IsEof() => _lexer.IsAtEnd;

    private bool NotEof() => !IsEof();

    /// <summary>
    /// The parser's position in the current line.
    /// Note: This is expensive to use.
    /// </summary>
    private uint Column() => IsEof() ? 0 : (uint)_lexer.GetColumn();

    /// <summary>
    /// tree-sitter's lexer interface maintains a current position that determines the lookahead character and the
    /// range of text that is associated with the symbol selected by the scanner, if <c>mark_end</c> is called.
    ///
    /// It's not possible to read earlier characters once the lexer has advanced over them, so this function appends
    /// the lookahead character to the array <c>lookahead</c> in the <c>State</c>.
    ///
    /// Don't add zeroes to the lookahead buffer when hitting EOF – it causes <c>no_lookahead</c> to report false
    /// negatives.
    /// </summary>
    private void Advance()
    {
        if (NotEof())
        {
            if (_lookaheadSize == _lookaheadContents.Length)
            {
                Array.Resize(ref _lookaheadContents, _lookaheadContents.Length * 2);
            }

            _lookaheadContents[_lookaheadSize++] = _lexer.Lookahead;
            _lexer.Advance(false);
        }
    }

    // S_SKIP: move the parser position one character to the right, treating the consumed character as whitespace.
    private void Skip() => _lexer.Advance(true);

    /// <summary>
    /// MARK: instruct the lexer that the current position is the end of the potentially detected symbol, causing the
    /// next run to be started after this character in the success case.
    ///
    /// This is useful if the validity of the detected symbol depends on what follows.
    /// </summary>
    private void Mark(string desc)
    {
        _ = desc;
        _lexer.MarkEnd();
    }

    private bool SetResultSymbol(Symbol result)
    {
        if (result != Symbol.Fail)
        {
            _lexer.ResultSymbol = (ushort)result;
            return true;
        }

        return false;
    }

    private bool Valid(Symbol s) => _symbols[(int)s];

    // --------------------------------------------------------------------------------------------------------
    // Symbol constructors
    // --------------------------------------------------------------------------------------------------------

    private static Symbol Finish(Symbol s, string desc)
    {
        // Suppress unused param warning
        _ = desc;
        return s;
    }

    private Symbol FinishIfValid(Symbol s, string desc)
    {
        if (Valid(s))
        {
            return Finish(s, desc);
        }

        return Symbol.Fail;
    }

    private Symbol FinishMarked(Symbol s, string desc)
    {
        Mark(desc);
        return s;
    }

    private static Symbol UpdateState(string desc) => Finish(Symbol.Update, desc);

    // --------------------------------------------------------------------------------------------------------
    // Lookahead
    // --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Ensure that at least <c>abs + 1</c> characters are present in the lookahead buffer by calling <c>advance</c>
    /// <c>len - abs + 1</c> times.
    /// </summary>
    private void AdvanceOverAbs(uint abs)
    {
        for (var i = _lookaheadSize; i <= abs; i++)
        {
            Advance();
        }
    }

    /// <summary>
    /// Ensure that at least <c>rel</c> characters after and including the current <c>offset</c> are present in the
    /// lookahead buffer by calling <c>advance</c> as often as the difference between the desired index
    /// (<c>offset + rel</c>) and one less than the current buffer size.
    ///
    /// Note: The character at the offset is included in the range, so that when <c>len == offset == rel == 0</c>, this
    /// function advances once, over the character at index 0.
    /// </summary>
    private void AdvanceOver(uint rel) => AdvanceOverAbs(_lookaheadOffset + rel);

    /// <summary>
    /// Skip whitespace relative to <c>offset</c>, but keep characters that have already been copied to the buffer.
    ///
    /// Hence, this function only skips indexes larger than the lookahead buffer's <c>len</c>.
    ///
    /// Additionally, if <c>offset</c> has been set to a position outside of the buffer, all characters up to that
    /// index are copied to the buffer beforehand.
    /// </summary>
    private void SkipOver(uint rel)
    {
        // Subtraction is safe because the condition establishes that `offset` is at least 1
        if (_lookaheadOffset > _lookaheadSize)
        {
            AdvanceOverAbs(_lookaheadOffset - 1);
        }

        var abs = _lookaheadOffset + rel;
        for (var i = _lookaheadSize; i <= abs; i++)
        {
            Skip();
        }
    }

    /// <summary>
    /// Ensure that the lookahead buffer is large enough to allow reading the <c>n</c>th character.
    /// Since <c>lexer->lookahead</c> points at the character after the buffer, it must have <c>offset + n - 1</c>
    /// elements.
    /// </summary>
    private void AdvanceBefore(uint rel)
    {
        var abs = _lookaheadOffset + rel;
        if (abs > 0)
        {
            AdvanceOverAbs(abs - 1);
        }
    }

    /// <summary>
    /// Return the lookahead character with index <c>n</c>.
    /// If the index is larger than the lookahead buffer, return 0.
    ///
    /// Unsafe insofar as that it does not advance if the index points outside of the lookahead buffer.
    /// This may happen in regular operation when a tool like <c>seq</c> attempts to look beyond EOF.
    /// </summary>
    private int UnsafePeekAbs(uint abs) => abs < _lookaheadSize ? _lookaheadContents[abs] : 0;

    /// <summary>Return the lookahead character with index <c>offset + n</c>. See <c>unsafe_peek_abs</c>.</summary>
    private int UnsafePeek(uint rel) => UnsafePeekAbs(_lookaheadOffset + rel);

    /// <summary>
    /// Return the lookahead character with index <c>offset + rel</c>.
    /// If the character is not accessible, advance the position until it is.
    ///
    /// This "peeks" insofar as it doesn't advance over the requested character – <c>peek(0)</c> is equivalent to
    /// <c>lexer->lookahead</c> if <c>offset == 0</c>.
    /// </summary>
    private int Peek(uint rel)
    {
        if (_lookaheadOffset + rel < _lookaheadSize)
        {
            return UnsafePeek(rel);
        }
        else
        {
            AdvanceBefore(rel);
            return _lexer.Lookahead;
        }
    }

    /// <summary>Return the first lookahead character after the <c>offset</c> without advancing the position.</summary>
    private int Peek0() => Peek(0);

    /// <summary>
    /// Return the second lookahead character after the <c>offset</c> without advancing the position further than the
    /// first character.
    /// </summary>
    private int Peek1() => Peek(1);

    /// <summary>
    /// Return the third lookahead character after the <c>offset</c> without advancing the position further than the
    /// second character.
    /// </summary>
    private int Peek2() => Peek(2);

    /// <summary>Test the lookahead character at index <c>offset + n</c> for equality.</summary>
    private bool CharAt(uint n, int c) => Peek(n) == c;

    /// <summary>Test the lookahead character at index <c>offset</c> for equality.</summary>
    private bool Char0(int c) => CharAt(0, c);

    /// <summary>Test the lookahead character at index <c>offset + 1</c> for equality.</summary>
    private bool Char1(int c) => CharAt(1, c);

    /// <summary>Test the lookahead character at index <c>offset + 2</c> for equality.</summary>
    private bool Char2(int c) => CharAt(2, c);

    /// <summary>
    /// Set the offset to <c>index</c>, so that the indexes in future calls to lookahead functions like <c>char0</c>
    /// are interpreted relative to this new value.
    ///
    /// Resets <c>symop</c> for soundness, even though no rule would continue after advancing over symbolic characters.
    /// </summary>
    private void ResetLookaheadAbs(uint abs)
    {
        _lookaheadOffset = abs;
        _symop = 0;
    }

    private void ResetLookaheadTo(uint rel) => ResetLookaheadAbs(_lookaheadOffset + rel);

    /// <summary>
    /// Move <c>offset</c> to the end of the consumed lookahead, causing <c>peek</c>, <c>char0</c> etc. to operate on
    /// characters following the current position at the time this function is executed.
    /// </summary>
    private void ResetLookahead() => ResetLookaheadAbs(_lookaheadSize);

    /// <summary>
    /// Return whether the lookahead position has been advanced since starting the run, not considering skipped
    /// characters (which are usually whitespace).
    /// This is important to decide whether the scanner has to be restarted to emit certain symbols.
    /// </summary>
    private bool NoLookahead() => _lookaheadSize == 0;

    /// <summary>
    /// Return the column of the first lookahead character of the current run.
    /// This is needed for starting layouts in interior mode, since we don't count positions across interior runs.
    /// </summary>
    private uint StartColumn() => Column() - _lookaheadSize;

    /// <summary>
    /// Increment <c>i</c> while the predicate is true for the lookahead character at that index (relative to
    /// <c>offset</c>), advancing the position when <c>i</c> points beyond the end of the lookahead buffer.
    /// Return the index after the last matching character.
    /// </summary>
    private uint AdvanceWhile(uint i, Func<int, bool> pred)
    {
        while (pred(Peek(i)))
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Same as <c>advance_while</c>, using "not equal to <c>c</c>" for the predicate.
    /// Stops at EOF.
    /// </summary>
    private uint AdvanceUntilChar(uint i, int c)
    {
        while (NotEof() && !CharAt(i, c))
        {
            i++;
        }

        return i;
    }

    // --------------------------------------------------------------------------------------------------------
    // Context manipulation and conditions
    // --------------------------------------------------------------------------------------------------------

    private bool HasContexts() => _contextsSize != 0;

    /// <summary>Push a layout context onto the stack.</summary>
    private void PushContext(ContextSort sort, uint indent)
    {
        var ctx = new Context { Sort = sort, Indent = indent };
        if (_contextsSize == _contexts.Length)
        {
            Array.Resize(ref _contexts, _contexts.Length * 2);
        }

        _contexts[_contextsSize++] = ctx;
    }

    /// <summary>Remove a layout context from the stack.</summary>
    private void Pop()
    {
        if (HasContexts())
        {
            _contextsSize--;
        }
    }

    private ContextSort CurrentContext() => HasContexts() ? _contexts[_contextsSize - 1].Sort : ContextSort.NoContext;

    private bool IsLayoutContext() => CurrentContext() < ContextSort.Braces;

    /// <summary>
    /// Decide whether the current context requires generation of layout semicolons.
    /// This is true for all layout contexts except for multi-way if, since that uses <c>|</c> to start layout
    /// elements.
    /// </summary>
    private bool IsSemicolonContext() => CurrentContext() < ContextSort.MultiWayIfLayout;

    /// <summary>
    /// Return the indent of the innermost layout context.
    /// If there are non-layout contexts at the top of the stack, search downwards.
    /// </summary>
    private uint CurrentIndent()
    {
        for (var i = (int)_contextsSize - 1; i >= 0; i--)
        {
            ref var cur = ref _contexts[i];
            if (cur.Sort < ContextSort.Braces)
            {
                return cur.Indent;
            }
        }

        return 0;
    }

    private bool IndentLess(uint indent) => IsLayoutContext() && indent < CurrentIndent();

    private bool IndentLesseq(uint indent) => IsLayoutContext() && indent <= CurrentIndent();

    private bool TopLayout() => _contextsSize == 1;

    private bool InModuleHeader() => CurrentContext() == ContextSort.ModuleHeader;

    /// <summary>Return the appropriate symbol to close the given context, or FAIL if it can't be closed.</summary>
    private static Symbol ContextEndSym(ContextSort s)
    {
        switch (s)
        {
            case ContextSort.TExp:
                return Symbol.EndTexp;
            case ContextSort.Braces:
                return Symbol.EndBrace;
            default:
                return s < ContextSort.Braces ? Symbol.End : Symbol.Fail;
        }
    }

    // --------------------------------------------------------------------------------------------------------
    // Character and lookahead conditions
    // --------------------------------------------------------------------------------------------------------

    private static bool IsNewline(int c)
    {
        switch (c)
        {
            case '\n':
            case '\r':
            case '\f':
                return true;
            default:
                return false;
        }
    }

    private static bool VaridStartChar(int c) => c == '_' || IsVaridStartChar(c);

    // TODO This should be combined with is_inner_id_char and made more explicit about when which char can occur.
    // For example, lex_symop uses this to decide about prefix dot being a field selector, where single quotes aren't valid.
    private static bool IsIdChar(int c) => c == '_' || c == '\'' || IsIdentifierChar(c);

    // TODO hashes only work at the end of identifiers
    private static bool IsInnerIdChar(int c) => IsIdChar(c) || c == '#';

    private static bool QuoterChar(int c) => IsIdChar(c) || c == '.';

    private static bool ReservedSymbolic(int c)
    {
        switch (c)
        {
            case '(':
            case ')':
            case ',':
            case ';':
            case '[':
            case ']':
            case '`':
            case '{':
            case '}':
            case '"':
            case '\'':
            case '_':
                return true;
            default:
                return false;
        }
    }

    private static bool SymopChar(int c) => IsSymopChar(c) && !ReservedSymbolic(c);

    /// <summary>
    /// Advance the position to the first character that's not valid for a symbolic operator, and return that position.
    /// If the function has been called before, directly return the cached position.
    ///
    /// This consumes the entire symop, since the field denotes the length of the string and therefore the last
    /// (failing) peek is <i>beyond</i> the end, consuming the last valid char.
    /// </summary>
    private uint SymopLookahead()
    {
        if (_symop == 0)
        {
            _symop = AdvanceWhile(0, SymopChar);
        }

        return _symop;
    }

    private bool IsSymop() => SymopLookahead() > 0;

    /// <summary>
    /// The parser calls <c>scan</c> with all symbols declared as valid directly after it encountered an error.
    /// The symbol <c>FAIL</c> is not used in the grammar, so it can only be valid in this error case.
    /// </summary>
    private bool AfterError() => Valid(Symbol.Fail);

    // --------------------------------------------------------------------------------------------------------
    // Lookahead
    // --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Check if lookahead contains the string <c>s</c> starting at position <c>offset + start</c>.
    /// This advances only over matching characters.
    /// </summary>
    private bool SeqFrom(string s, uint start)
    {
        var len = (uint)s.Length;
        for (var i = 0u; i < len; i++)
        {
            int c = s[(int)i];
            var c2 = Peek(start + i);
            if (c != c2)
            {
                return false;
            }
        }

        Peek(start + len);
        return true;
    }

    /// <summary>Check if lookahead contains the string <c>s</c> starting at position <c>offset</c>.</summary>
    private bool Seq(string s) => SeqFrom(s, 0);

    /// <summary>Advance until the next newline or EOF, used to consume the body of a comment.</summary>
    private void TakeLine()
    {
        while (NotEof() && !IsNewline(_lexer.Lookahead))
        {
            Advance();
        }
    }

    private static bool IsSpaceOrTab(int c) => c == ' ' || c == '\t';

    /// <summary>
    /// Advance until the next newline or EOF, used to consume the body of a cpp directive.
    /// Escaped newlines are treated as line continuations, which allow spaces and tabs between backslash and newline.
    /// </summary>
    private void TakeLineEscapedNewline()
    {
        for (; ; )
        {
            while (NotEof() && !IsNewline(_lexer.Lookahead) && _lexer.Lookahead != '\\')
            {
                Advance();
            }

            if (_lexer.Lookahead == '\\')
            {
                Advance();
                if (IsSpaceOrTab(_lexer.Lookahead))
                {
                    while (IsSpaceOrTab(_lexer.Lookahead))
                    {
                        Advance();
                    }

                    if (IsNewline(_lexer.Lookahead))
                    {
                        Advance();
                    }
                }
                else
                {
                    Advance();
                }
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>
    /// Skip the lexer until the following character is neither space nor tab.
    /// Return whether any characters were skipped.
    /// </summary>
    private bool SkipSpace()
    {
        if (!IsSpaceChar(_lexer.Lookahead))
        {
            return false;
        }

        Skip();
        while (IsSpaceChar(_lexer.Lookahead))
        {
            Skip();
        }

        return true;
    }

    /// <summary>
    /// Skip the lexer until the following character is not a newline.
    /// Return whether any characters were skipped.
    /// </summary>
    private bool SkipNewlines()
    {
        if (!IsNewline(_lexer.Lookahead))
        {
            return false;
        }

        Skip();
        while (IsNewline(_lexer.Lookahead))
        {
            Skip();
        }

        return true;
    }

    private enum Space
    {
        NoSpace,
        Indented,
        BOL,
    }

    /// <summary>
    /// Alternate between skipping space and newlines, and return which was seen last.
    /// This does not use the lookahead buffer, but directly accesses the lexer.
    /// Only to be used when it is certain that no whitespace has been copied to the buffer by previous steps, and that
    /// no previous characters should be included in the range of non-zero-width symbol.
    /// </summary>
    private Space SkipWhitespace()
    {
        var space = Space.NoSpace;
        while (true)
        {
            if (SkipSpace())
            {
                space = Space.Indented;
            }
            else if (SkipNewlines())
            {
                space = Space.BOL;
            }
            else
            {
                return space;
            }
        }
    }

    /// <summary>
    /// Advance the lexer until the following character is neither space nor tab, starting at position
    /// <c>offset + start</c>, and return the index of the next character.
    /// </summary>
    private uint TakeSpaceFrom(uint start) => AdvanceWhile(start, IsSpaceChar);

    /// <summary>
    /// Ensure that the character after a keyword like <c>module</c> is not a character that would change its meaning
    /// to be an identifier.
    /// </summary>
    private static bool TokenEnd(int c) => !IsInnerIdChar(c);

    /// <summary>
    /// Check if lookahead contains the string <c>s</c> starting at position <c>offset + start</c>, followed by a
    /// non-id character.
    /// See <c>seq</c>.
    /// </summary>
    private bool TokenFrom(string s, uint start) => SeqFrom(s, start) && TokenEnd(Peek(start + (uint)s.Length));

    /// <summary><c>token_from</c> at the current offset.</summary>
    private bool Token(string s) => Seq(s) && TokenEnd(Peek((uint)s.Length));

    /// <summary>
    /// Check if lookahead contains any of the strings in <c>tokens</c> starting at position <c>offset + start</c>,
    /// followed by a non-id character.
    /// </summary>
    private bool AnyTokenFrom(string[] tokens, uint start)
    {
        for (var i = 0; i < tokens.Length; i++)
        {
            if (TokenFrom(tokens[i], start))
            {
                return true;
            }
        }

        return false;
    }

    private bool MatchSymop(string target) => SymopLookahead() == target.Length && Seq(target);

    private bool Uninitialized() => !HasContexts();

    private uint Conid()
    {
        if (!IsConidStartChar(Peek0()))
        {
            return 0;
        }

        return AdvanceWhile(1, IsInnerIdChar);
    }

    // The C enum QualifiedName
    private enum QualifiedNameKind
    {
        NoQualifiedName,
        QualifiedTarget,
        QualifiedConid,
    }

    private QualifiedNameKind QualifiedName(Func<bool> name)
    {
        var qualified = false;
        while (true)
        {
            var end = Conid();
            if (end == 0)
            {
                break;
            }

            if (!CharAt(end, '.'))
            {
                if (qualified)
                {
                    return QualifiedNameKind.QualifiedConid;
                }
                else
                {
                    break;
                }
            }

            qualified = true;
            ResetLookaheadTo(end + 1);
            if (name())
            {
                // `return true`
                return QualifiedNameKind.QualifiedTarget;
            }
        }

        return QualifiedNameKind.NoQualifiedName;
    }

    /// <summary>Use the lookahead buffer to determine whether a character is escaped, by counting the number of backslashes.</summary>
    private bool OddBackslashesBefore(int index)
    {
        var odd = false;
        while (index >= 0 && Peek((uint)index) == '\\')
        {
            odd = !odd;
            index--;
        }

        return odd;
    }

    /// <summary>Advance before the next unescaped double quote.</summary>
    private uint TakeStringLiteral()
    {
        var end = 1u;
        while (true)
        {
            end = AdvanceUntilChar(end, '"') + 1;
            if (IsEof() || !OddBackslashesBefore((int)end - 2))
            {
                return end;
            }
        }
    }

    /// <summary>
    /// Advance before the single quote that validly ends a character literal.
    /// If there is none, return 1.
    /// Either the first character is a backslash, or the second character is a single quote.
    ///
    /// A single quote followed by backslash is a char unless it was part of a varid, in which case the backslash is a
    /// lambda.
    /// The caller must make sure to lex varids beforehand.
    /// </summary>
    private uint TakeCharLiteral()
    {
        if (Char1('\\'))
        {
            return AdvanceUntilChar(2, '\'') + 2;
        }
        else
        {
            return CharAt(2, '\'') ? 3u : 1u;
        }
    }

    // --------------------------------------------------------------------------------------------------------
    // Lookahead: CPP
    // --------------------------------------------------------------------------------------------------------

    // The C enum CppDirective
    private enum CppDirectiveKind
    {
        CppNothing,
        CppStart,
        CppElse,
        CppEnd,
        CppOther,
    }

    private static readonly string[] CppTokensStart =
    [
        "if",
        "ifdef",
        "ifndef",
    ];

    private bool CppCondStart(uint start) => AnyTokenFrom(CppTokensStart, start);

    private static readonly string[] CppTokensElse =
    [
        "else",
        "elif",
        "elifdef",
        "elifndef",
    ];

    private bool CppCondElse(uint start) => AnyTokenFrom(CppTokensElse, start);

    private bool CppCondEnd(uint start) => TokenFrom("endif", start);

    private static readonly string[] CppTokensOther =
    [
        "define",
        "undef",
        "include",
        "pragma",
        "error",
        "warning",
        "line",
    ];

    private bool CppDirectiveOther(uint start)
    {
        return
            AnyTokenFrom(CppTokensOther, start)
            ||

            // A hash followed by nothing but whitespace is CPP.
            // If non-whitespace follows whitespace, it is a parse error, unless we're in a brace layout; then it is a
            // varsym.
            // Complete overkill to parse this, but eh!
            IsNewline(Peek(start))
            ||

            // shebang for scripts
            (Char1('!') && Uninitialized());
    }

    /// <summary>
    /// If the first character at <c>offset</c> is a hash, skip space and try all tokens that start a CPP directive.
    /// Return the matching variant of the enum <c>CppDirective</c>.
    /// </summary>
    private CppDirectiveKind CppDirective()
    {
        if (!Char0('#'))
        {
            return CppDirectiveKind.CppNothing;
        }

        var start = TakeSpaceFrom(1);
        if (CppCondStart(start))
        {
            return CppDirectiveKind.CppStart;
        }
        else if (CppCondElse(start))
        {
            return CppDirectiveKind.CppElse;
        }
        else if (CppCondEnd(start))
        {
            return CppDirectiveKind.CppEnd;
        }
        else if (CppDirectiveOther(start))
        {
            return CppDirectiveKind.CppOther;
        }
        else
        {
            return CppDirectiveKind.CppNothing;
        }
    }

    // --------------------------------------------------------------------------------------------------------
    // Starting layouts
    // --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opening and closing braces are always followed by a command (<c>grammar/util.js</c>), so this can
    /// unconditionally push a context.
    /// See <c>grammar/externals.js</c> for more.
    ///
    /// Note: This is not related to regular brace layouts, which are handled by <c>start_layout</c>!
    /// Aside from layouts, braces are also used for records and inferred type variables, where indentation is also
    /// ignored!
    /// Therefore, we add a context to skip steps like semicolon generation.
    /// </summary>
    private Symbol StartBrace()
    {
        if (Valid(Symbol.StartBrace))
        {
            PushContext(ContextSort.Braces, 0);
            return Finish(Symbol.StartBrace, "start_brace");
        }

        return Symbol.Fail;
    }

    /// <summary>See <c>start_brace</c>.</summary>
    private Symbol EndBrace()
    {
        if (Valid(Symbol.EndBrace) && CurrentContext() == ContextSort.Braces)
        {
            Pop();
            return Finish(Symbol.EndBrace, "end_brace");
        }

        return Symbol.Fail;
    }

    /// <summary>Return the first valid layout start symbol.</summary>
    private Symbol ValidLayoutStartSym()
    {
        for (var i = Symbol.Start; i < Symbol.End; i++)
        {
            if (Valid(i))
            {
                return i;
            }
        }

        return Symbol.Fail;
    }

    /// <summary>Map <c>Symbol</c> to <c>ContextSort</c>.</summary>
    private static ContextSort LayoutSort(Symbol s)
    {
        switch (s)
        {
            case Symbol.StartDo:
                return ContextSort.DoLayout;
            case Symbol.StartCase:
                return ContextSort.CaseLayout;
            case Symbol.StartIf:
                return ContextSort.MultiWayIfLayout;
            case Symbol.StartLet:
                return ContextSort.LetLayout;
            case Symbol.StartQuote:
                return ContextSort.QuoteLayout;
            default:
                return ContextSort.DeclLayout;
        }
    }

    // The C struct StartLayout
    private struct LayoutStart
    {
        public Symbol Sym;
        public ContextSort Sort;
    }

    /// <summary>
    /// Determine whether the layout sort corresponding to the potentially valid symbol can start at this position.
    /// If the context stack is <c>uninitialized</c>, the first layout is added by <c>process_token_init</c>.
    /// In newline processing mode, brace layouts cannot be started because there may be comments before the brace that
    /// need to be emitted first.
    /// Regular <c>if/then/else</c> conditionals are always valid at the same position as multi-way if layouts.
    /// If we were to unconditionally start a layout when START_IF is valid, it would never be possible to parse the
    /// former, so this skips that layout sort unless the <c>Lexed</c> token is <c>LBar</c>.
    /// </summary>
    private LayoutStart ValidLayoutStart(Lexed next)
    {
        var start = new LayoutStart { Sym = ValidLayoutStartSym(), Sort = ContextSort.NoContext };
        if (Uninitialized() || start.Sym == Symbol.Fail)
        {
            return start;
        }

        var sort = LayoutSort(start.Sym);
        switch (next)
        {
            case Lexed.LBar:
                break;
            case Lexed.LBraceOpen:
                if (NewlineActive())
                {
                    return start;
                }

                sort = ContextSort.Braces;
                start.Sym = Symbol.StartExplicit;
                break;
            default:
                if (sort == ContextSort.MultiWayIfLayout)
                {
                    return start;
                }

                break;
        }

        start.Sort = sort;
        return start;
    }

    /// <summary>
    /// If the current context is a brace layout, any indent is legal for a new layout.
    /// Otherwise, compare with the indent of the current context.
    /// Since starting layouts is allowed in tuple expressions, we look at the last real indent, skipping over
    /// <c>TExp</c>s, using 0 if none exists (which should never be the case).
    ///
    /// According to the docs for <c>NondecreasingIndentation</c>, the rule is that a nested context may start at the
    /// same column <i>if the enclosing context is a do expression</i>.
    /// From experimental evidence, it appears though that this is the other way round – a do expression within, say, a
    /// case alt can start at the same level as the case layout.
    /// </summary>
    private bool IndentCanStartLayout(ContextSort sort, uint indent)
    {
        if (CurrentContext() == ContextSort.Braces)
        {
            return true;
        }

        var cur = CurrentIndent();
        return indent > cur || (indent == cur && sort == ContextSort.DoLayout);
    }

    /// <summary>
    /// Start the given layout if the position allows it:
    ///
    /// - If the current context is <c>ModuleHeader</c>, the layout must be the <c>where</c> after <c>module</c>, so any
    ///   indent is valid.
    ///
    /// - If the new layout is a brace layout, legal indent is technically required, but we can be lenient since there's
    ///   no other way to interpret an opening brace after a layout opener.
    ///   However, we need to mark to include the brace in the range to create a terminal (see
    ///   <c>grammar/externals.js</c> for why).
    ///
    /// - Otherwise, examine indent.
    /// </summary>
    private Symbol StartLayout(LayoutStart start, uint indent, string desc)
    {
        if (InModuleHeader())
        {
            Pop();
        }
        else if (start.Sort == ContextSort.Braces)
        {
            Mark("start_layout brace");
        }
        else if (!IndentCanStartLayout(start.Sort, indent))
        {
            return Symbol.Fail;
        }

        PushContext(start.Sort, indent);
        return Finish(start.Sym, desc);
    }

    /// <summary>
    /// The indent of a layout started at an interior token can only be determined by calling <c>get_column</c>.
    /// This is an expensive operation, but hopefully it is rare enough to not make a substantial dent.
    /// Because we might have advanced beyond the layout's start position to check conditions, we need to subtract the
    /// length of the lookahead buffer from the current column.
    /// Whitespace is skipped, and not added to the buffer, so the resulting position is after whitespace.
    /// </summary>
    private Symbol StartLayoutInterior(Lexed next)
    {
        var start = ValidLayoutStart(next);
        if (start.Sort == ContextSort.NoContext)
        {
            return Symbol.Fail;
        }

        return StartLayout(start, StartColumn(), "interior");
    }

    /// <summary>
    /// The indent of a layout started at the beginning of a line is determined by <c>newline_lookahead</c>, so this
    /// does not have to compute it.
    /// </summary>
    private Symbol StartLayoutNewline()
    {
        var start = ValidLayoutStart(_newline.End);
        if (start.Sort == ContextSort.NoContext)
        {
            return Symbol.Fail;
        }

        var result = StartLayout(start, _newline.Indent, "newline");
        if (result != Symbol.Fail)
        {
            _newline.NoSemi = true;
        }

        return result;
    }

    /// <summary>See <c>token_end_layout_texp</c>.</summary>
    private Symbol TexpContext()
    {
        if (Valid(Symbol.StartTexp))
        {
            PushContext(ContextSort.TExp, 0);
            return Finish(Symbol.StartTexp, "texp_context");
        }
        else if (Valid(Symbol.EndTexp) && CurrentContext() == ContextSort.TExp)
        {
            Pop();
            return Finish(Symbol.EndTexp, "texp_context");
        }
        else
        {
            return Symbol.Fail;
        }
    }

    // --------------------------------------------------------------------------------------------------------
    // Ending layouts
    // --------------------------------------------------------------------------------------------------------

    /// <summary>Separated this from <c>end_layout</c> because it caused some weird performance glitches.</summary>
    private Symbol EndLayoutUnchecked(string desc)
    {
        Pop();
        return Finish(Symbol.End, desc);
    }

    /// <summary>If a layout end is valid at this position, pop a context and succeed with layout end.</summary>
    private Symbol EndLayout(string desc)
    {
        if (Valid(Symbol.End))
        {
            return EndLayoutUnchecked(desc);
        }
        else
        {
            return Symbol.Fail;
        }
    }

    /// <summary>
    /// Explicit brace layouts need a dedicated symbol, see <c>_cmd_layout_start_explicit</c> for an explanation.
    /// Includes the brace in the range.
    /// </summary>
    private Symbol EndLayoutBrace()
    {
        if (Valid(Symbol.EndExplicit) && CurrentContext() == ContextSort.Braces)
        {
            AdvanceOver(0);
            Mark("end_layout_brace");
            Pop();
            return Finish(Symbol.EndExplicit, "brace");
        }
        else
        {
            return Symbol.Fail;
        }
    }

    /// <summary>
    /// End a layout based on indent decrease.
    ///
    /// If the indent of the current line is smaller than the indent of the current layout, we end the layout in most
    /// cases.
    /// Exceptions are:
    ///
    /// - Brace layouts
    /// - The top-level layout, which should only be ended at the end of file.
    ///   For leniency, we change the current indent to the smaller value.
    /// </summary>
    private Symbol EndLayoutIndent()
    {
        if (Valid(Symbol.End) && IndentLess(_newline.Indent))
        {
            if (TopLayout())
            {
                _contexts[_contextsSize - 1].Indent = _newline.Indent;
                return UpdateState("end top layout");
            }
            else
            {
                _newline.SkipSemi = false;
                return EndLayoutUnchecked("indent");
            }
        }

        return Symbol.Fail;
    }

    /// <summary>
    /// An expression layout may be closed by an infix operator when it is not valid at that position: the indent of the
    /// operator causes a semicolon to be generated, which leads to varsym being invalid lookahead.
    /// The layout is closed and the entire <c>do</c> block becomes the left operand of the operator.
    /// The same applies for <c>infix</c> id operators.
    ///
    /// It doesn't apply to multi-way if layouts, because those don't use semicolons.
    /// </summary>
    private Symbol EndLayoutInfix()
    {
        if (!Valid(Symbol.Varsym) && !Valid(Symbol.Consym))
        {
            return EndLayout("symop invalid");
        }

        return Symbol.Fail;
    }

    /// <summary>
    /// A case alt can have a <c>where</c> clause attached to it, so a case layout is ended by a <c>where</c> only if its
    /// indent is equal to or smaller than the layout indent.
    ///
    /// A <c>do</c> or <c>if</c> cannot have a <c>where</c>, so they are always terminated.
    ///
    /// By tracking the validity of <c>where</c> symbols, we can simplify the condition for <c>do</c> and <c>if</c>: End
    /// any layout when <c>where</c> is parsed, but invalid.
    /// </summary>
    private Symbol EndLayoutWhere()
    {
        if (Valid(Symbol.End) && !Valid(Symbol.Where) && IsLayoutContext())
        {
            return EndLayout("where");
        }

        return Symbol.Fail;
    }

    /// <summary>
    /// Ending layouts with <c>in</c> heavily relies on parse errors in GHC, so this is difficult.
    /// The heuristic here is that if <c>in</c> is not valid (i.e. a parse error), we pop any layout.
    ///
    /// Note that this doesn't allow the <c>in</c> of a nested <c>let</c> to close the outer <c>let</c>, since the
    /// <c>END</c> for that isn't valid before the inner <c>let</c> has reduced.
    /// </summary>
    private Symbol EndLayoutIn()
    {
        if (Valid(Symbol.End) && (!Valid(Symbol.In) || CurrentContext() == ContextSort.LetLayout))
        {
            return EndLayout("in");
        }

        return Symbol.Fail;
    }

    /// <summary>For GADT constructor layouts.</summary>
    private Symbol EndLayoutDeriving()
    {
        if (Valid(Symbol.End) && !Valid(Symbol.Deriving) && !TopLayout() && CurrentContext() == ContextSort.DeclLayout)
        {
            return EndLayout("deriving");
        }

        return Symbol.Fail;
    }

    /// <summary>Return <c>true</c> if there is a <c>TExp</c> context on the stack and only layouts above it.</summary>
    private bool LayoutsInTexp()
    {
        if (IsLayoutContext() && (_contextsSize > 1))
        {
            for (var i = (int)_contextsSize - 2; i >= 0; i--)
            {
                ref var cur = ref _contexts[i];
                if (cur.Sort == ContextSort.TExp || cur.Sort == ContextSort.Braces)
                {
                    return true;
                }
                else if (cur.Sort > ContextSort.Braces)
                {
                    break;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Tuple expressions are constructs that syntactically delimit their contents in an unambiguous way that makes
    /// parsing a lot easier.
    /// In practice, it means that expression layouts can be closed by commas, vertical bars and closing brackets and
    /// parens when they are elements in a list or tuple-like construct.
    /// We encode this as a special context sort, <c>TExp</c>, that is pushed and popped at opening and closing
    /// brackets.
    /// </summary>
    private Symbol TokenEndLayoutTexp() => (Valid(Symbol.End) && LayoutsInTexp()) ? EndLayout("texp") : Symbol.Fail;

    private Symbol ForceEndContext()
    {
        for (var i = (int)_contextsSize - 1; i >= 0; i--)
        {
            var ctx = _contexts[i].Sort;
            var s = ContextEndSym(ctx);
            Pop();
            if (s != Symbol.Fail && Valid(s))
            {
                return Finish(s, "force_end_context");
            }
        }

        return Symbol.Fail;
    }

    // --------------------------------------------------------------------------------------------------------
    // Operators
    // --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opening tokens are a class of characters that may immediately follow prefix operators like bang pattern
    /// <c>!</c> or type application <c>@</c>.
    /// </summary>
    private bool OpeningToken(uint i)
    {
        var c = Peek(i);
        switch (c)
        {
            case 0x27e6: // ⟦
            case 0x2987: // ⦇
            case '(':
            case '[':
            case '"':
                return true;
            case '{':
                return Peek(i + 1) != '-';
            default:
                // Includes single quote
                return IsIdChar(c);
        }
    }

    /// <summary>Test for reserved operators of two characters.</summary>
    private static bool ValidSymopTwoChars(int firstChar, int secondChar)
    {
        switch (firstChar)
        {
            case '=':
                return secondChar != '>';
            case '<':
                return secondChar != '-';
            case ':':
                return secondChar != ':';
            default:
                return true;
        }
    }

    /// <summary>If a prefix operator is not followed by an opening token, it may still be a valid varsym.</summary>
    private Lexed LexPrefix(Lexed t) => OpeningToken(1) ? t : Lexed.LSymop;

    /// <summary>
    /// If a splice operator is not followed by an opening token, it may still be a valid varsym.
    /// We only allow variables and parenthesized expressions for performance reasons, though.
    /// </summary>
    private static Lexed LexSplice(int c) => VaridStartChar(c) || c == '(' ? Lexed.LDollar : Lexed.LSymop;

    /// <summary>
    /// Lex special occurrences of symbolic operator characters, or declare a valid operator.
    ///
    /// For the dot:
    ///
    /// - Two dots: <c>..</c>: Only used for arithmetic sequences (<c>[a..10]</c>).
    ///   These conflict with record field projection (<c>[a.b, c]</c>) and infix operators (<c>[a..+b]</c>), and it's
    ///   too hard to disambiguate them without this special rule.
    ///
    /// - Tight dot <c>a.b.c</c>: A regular tight op, but it has to get a separate symbol from qualified module dots
    ///   since those can be followed by symops.
    ///
    /// - Prefix dot <c>(.a)</c>: A regular prefix op, for record dot field selectors.
    ///
    /// - Qualified dot <c>A.B.c</c>, <c>A.B.C</c>, <c>A.B.+</c>: Used primarily for qualified modules, but needs to be
    ///   accepted for field selectors as well due to ambiguity.
    ///   This is not a regular tight op since it needs to allow symops and conid.
    /// </summary>
    private Lexed LexSymop()
    {
        var len = SymopLookahead();
        if (len == 0)
        {
            return Lexed.LNothing;
        }

        var c1 = UnsafePeek(0);
        if (len == 1)
        {
            switch (c1)
            {
                case '?':
                    // A `?` can be the head of an implicit parameter, if followed by a varid.
                    return VaridStartChar(Peek1()) ? Lexed.LNothing : Lexed.LSymop;
                case '#':
                    return Char1(')') ? Lexed.LUnboxedClose : Lexed.LHash;
                case '|':
                    return Char1(']') ? Lexed.LQuoteClose : Lexed.LBar;
                case '!':
                    return LexPrefix(Lexed.LBang);
                case '~':
                    return LexPrefix(Lexed.LTilde);
                case '@':
                    return LexPrefix(Lexed.LAt);
                case '%':
                    return LexPrefix(Lexed.LPercent);
                case '$':
                    return LexSplice(Peek1());
                case '.':
                    if (IsIdChar(Peek1()))
                    {
                        return Lexed.LDotId;
                    }
                    else if (OpeningToken(1))
                    {
                        return Lexed.LDotOpen;
                    }
                    else
                    {
                        return Lexed.LSymop;
                    }

                case 0x2192: // →
                case 0x22b8: // ⊸
                    return Lexed.LArrow;
                case 0x21d2: // ⇒
                    return Lexed.LCArrow;
                case '=':
                case 0x27e7: // ⟧
                case 0x2988: // ⦈
                    return Lexed.LTexpCloser;
                case '*':
                case '-':
                    return Lexed.LSymopSpecial;
                case '\\':
                case 0x2190: // ←
                case 0x2200: // ∀
                case 0x2237: // ∷
                case 0x2605: // ★
                case 0x27e6: // ⟦
                case 0x2919: // ⤙
                case 0x291a: // ⤚
                case 0x291b: // ⤛
                case 0x291c: // ⤜
                case 0x2987: // ⦇
                    return Lexed.LNothing;
            }
        }
        else if (len == 2)
        {
            if (Seq("->"))
            {
                return Lexed.LArrow;
            }

            if (Seq("=>"))
            {
                return Lexed.LCArrow;
            }

            var c2 = UnsafePeek(1);
            switch (c1)
            {
                case '$':
                    if (c2 == '$')
                    {
                        return LexSplice(Peek2());
                    }

                    break;
                case '|':
                    if (c2 == '|' && Char2(']'))
                    {
                        return Lexed.LQuoteClose;
                    }

                    break;
                case '.':
                    if (c2 == '.')
                    {
                        return Lexed.LDotDot;
                    }
                    else
                    {
                        return Lexed.LDotSymop;
                    }

                case '#':
                    // Unboxed unit `(##)` and unboxed sum with missing space `(#| Int #)`
                    if (c2 == '#' || c2 == '|')
                    {
                        return Lexed.LSymopSpecial;
                    }

                    break;
                default:
                    if (!ValidSymopTwoChars(c1, c2))
                    {
                        return Lexed.LNothing;
                    }

                    break;
            }
        }
        else
        {
            switch (c1)
            {
                case '-':
                    if (Seq("->."))
                    {
                        return Lexed.LArrow;
                    }

                    break;
                case '.':
                    return Lexed.LDotSymop;
            }
        }

        return Lexed.LSymop;
    }

    /// <summary>
    /// If the next character after whitespace starting from <c>start</c> is a closing parenthesis, finish with
    /// <c>LEFT_SECTION_OP</c>.
    /// This is called after a previous step has already lexed a valid infix operator (symbolic or ticked varid).
    ///
    /// Left section operators must be detected here to disambiguate from infix expressions.
    ///
    /// When this function finds any whitespace before the parenthesis, it will finish with <c>NO_SECTION_OP</c>, so
    /// that <c>LEFT_SECTION_OP</c> won't be valid in the next run, but all other infix operators are.
    /// </summary>
    private Symbol LeftSectionOp(uint start)
    {
        if (Valid(Symbol.LeftSectionOp))
        {
            AdvanceBefore(start);
            var space = SkipWhitespace();
            if (CharAt(start, ')'))
            {
                return Finish(Symbol.LeftSectionOp, "left section");
            }

            if (space != Space.NoSpace)
            {
                return FinishIfValid(Symbol.NoSectionOp, "left section");
            }
        }

        return Symbol.Fail;
    }

    /// <summary>Specialization of <c>left_section_op</c> for ticked infix identifiers.</summary>
    private Symbol LeftSectionTicked()
    {
        if (Valid(Symbol.LeftSectionOp))
        {
            var endTick = AdvanceUntilChar(1, '`');

            // Could be EOF
            if (CharAt(endTick, '`'))
            {
                return LeftSectionOp(endTick + 1);
            }
        }

        return Symbol.Fail;
    }

    /// <summary>
    /// This calls <c>symop_lookahead</c> to ensure that the position has advanced beyond the end of the symop, which is
    /// necessary because newline lookahead may have validated the symop in a previous run.
    /// This marks the range to emit a terminal.
    /// </summary>
    private Symbol FinishSymop(Symbol s)
    {
        if (Valid(s) || Valid(Symbol.LeftSectionOp))
        {
            var afterSymop = SymopLookahead();
            if (LeftSectionOp(afterSymop) is not Symbol.Fail and var res)
            {
                return res;
            }

            Mark("symop");
            return s;
        }

        return Symbol.Fail;
    }

    /// <summary>Tight ops like <c>dot.syntax</c> require that no initial whitespace was skipped.</summary>
    private Symbol TightOp(bool whitespace, Symbol s)
    {
        if (!whitespace)
        {
            return FinishIfValid(s, "tight_op");
        }
        else
        {
            return Symbol.Fail;
        }
    }

    /// <summary>
    /// Used for situations where the operator is followed by an opening token, and so can be a prefix op if it is
    /// preceded by whitespace; but is no valid tight op and therefore becomes a regular operator if not preceded by
    /// whitespace or the symbol is not valid.
    ///
    /// Only used for <c>%</c> (modifier).
    /// </summary>
    private Symbol PrefixOrVarsym(bool whitespace, Symbol s)
    {
        if (whitespace && FinishIfValid(s, "prefix_or_varsym") is not Symbol.Fail and var res)
        {
            return res;
        }

        return FinishSymop(Symbol.Varsym);
    }

    /// <summary>
    /// Used for situations where the operator is followed by an opening token, and so can be a tight op if it is not
    /// preceded by whitespace; but is no valid prefix op and therefore becomes a regular operator if preceded by
    /// whitespace or the symbol is not valid.
    ///
    /// Only used for <c>.</c>, when a projection selector <c>(.fieldname)</c> is not valid at this position, so the
    /// dot becomes the composition operator.
    /// </summary>
    private Symbol TightOrVarsym(bool whitespace, Symbol s)
    {
        if (TightOp(whitespace, s) is not Symbol.Fail and var res)
        {
            return res;
        }

        return FinishSymop(Symbol.Varsym);
    }

    /// <summary>
    /// Used for situations where the operator is followed by an opening token, and so can be a tight op if it is not
    /// preceded by whitespace or a prefix op if it is.
    ///
    /// If neither of those symbols is valid, fall back to a regular operator.
    ///
    /// Used for <c>!</c>, <c>~</c> and <c>@</c>.
    /// </summary>
    private Symbol InfixOrVarsym(bool whitespace, Symbol prefix, Symbol tight)
    {
        if (FinishIfValid(whitespace ? prefix : tight, "infix_or_varsym") is not Symbol.Fail and var res)
        {
            return res;
        }

        return FinishSymop(Symbol.Varsym);
    }

    private Symbol QualifiedOp()
    {
        if (QualifiedName(_isSymop) == QualifiedNameKind.QualifiedTarget)
        {
            if (LeftSectionOp(SymopLookahead()) is not Symbol.Fail and var res)
            {
                return res;
            }

            return Symbol.QualifiedOp;
        }

        return Symbol.Fail;
    }

    // --------------------------------------------------------------------------------------------------------
    // Token lookahead
    // --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Detect the start of a quasiquote: An opening bracket followed by an optional varid and a vertical bar, all
    /// without whitespace in between.
    /// </summary>
    private bool IsQqStart()
    {
        var end = AdvanceWhile(1, QuoterChar);
        return CharAt(end, '|');
    }

    /// <summary>An end token is a keyword like <c>else</c> or <c>deriving</c> that can end a layout without newline or indent.</summary>
    private Lexed TryEndToken(string target, Lexed match)
    {
        if (Token(target))
        {
            return match;
        }
        else
        {
            return Lexed.LNothing;
        }
    }

    /// <summary>Check that a symop consists only of minuses after the second character.</summary>
    private bool OnlyMinus()
    {
        var i = 2u;
        while (Peek(i) == '-')
        {
            i++;
        }

        return !SymopChar(Peek(i));
    }

    /// <summary>Check that a symop consists only of minuses, making it a comment herald.</summary>
    private bool LineCommentHerald() => Seq("--") && OnlyMinus();

    private Lexed LexCpp()
    {
        switch (CppDirective())
        {
            case CppDirectiveKind.CppElse:
                return Lexed.LCppElse;
            case CppDirectiveKind.CppNothing:
                return Lexed.LNothing;
            default:
                return Lexed.LCpp;
        }
    }

    /// <summary>Lex pragmas, comments and CPP.</summary>
    private Lexed LexExtras(bool bol)
    {
        switch (Peek0())
        {
            case '{':
                if (Char1('-'))
                {
                    return Char2('#') ? Lexed.LPragma : Lexed.LBlockComment;
                }

                break;
            case '#':
                if (bol)
                {
                    return LexCpp();
                }

                break;
            case '-':
                if (LineCommentHerald())
                {
                    return Lexed.LLineComment;
                }

                break;
            default:
                break;
        }

        return Lexed.LNothing;
    }

    /// <summary>
    /// The main lexing entry point, branching on the first character, then advancing as far as necessary to identify
    /// all interesting tokens.
    /// </summary>
    private Lexed Lex(bool bol)
    {
        if (LexExtras(bol) is not Lexed.LNothing and var extras)
        {
            return extras;
        }

        if (SymopChar(Peek0()))
        {
            if (LexSymop() is not Lexed.LNothing and var symop)
            {
                return symop;
            }
        }
        else
        {
            switch (Peek0())
            {
                case 'w':
                    return TryEndToken("where", Lexed.LWhere);
                case 'i':
                    return TryEndToken("in", Lexed.LIn);
                case 't':
                    return TryEndToken("then", Lexed.LThen);
                case 'e':
                    return TryEndToken("else", Lexed.LElse);
                case 'd':
                    return TryEndToken("deriving", Lexed.LDeriving);
                case 'm':
                    if ((Uninitialized() || InModuleHeader()) && Token("module"))
                    {
                        return Lexed.LModule;
                    }

                    break;
                case '{':
                    return Lexed.LBraceOpen;
                case '}':
                    return Lexed.LBraceClose;
                case ';':
                    return Lexed.LSemi;
                case '`':
                    return Lexed.LTick;
                case '[':
                    if (Valid(Symbol.QqStart) && IsQqStart())
                    {
                        return Lexed.LBracketOpen;
                    }

                    break;
                case ']':
                case ')':
                case ',':
                    return Lexed.LTexpCloser;
                default:
                    if (IsConidStartChar(Peek0()))
                    {
                        return Lexed.LUpper;
                    }

                    break;
            }
        }

        return Lexed.LNothing;
    }

    // --------------------------------------------------------------------------------------------------------
    // CPP
    // --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// This tests for <c>#endif</c> directly after taking a line, so it only matches it at the first column.
    /// Int finishes right before the <c>#endif</c>, so that pragma is parsed by <c>cpp_consume</c> in the next round.
    /// </summary>
    private Symbol CppElse(bool emit)
    {
        var nesting = 1u;
        do
        {
            TakeLineEscapedNewline();
            if (emit)
            {
                Mark("cpp_else");
            }

            Advance();
            ResetLookahead();
            switch (CppDirective())
            {
                case CppDirectiveKind.CppStart:
                    nesting++;
                    break;
                case CppDirectiveKind.CppEnd:
                    nesting--;
                    break;
                default:
                    break;
            }
        }
        while (NotEof() && nesting > 0);
        if (emit)
        {
            return Finish(Symbol.Cpp, "cpp-else");
        }
        else
        {
            return Symbol.Fail;
        }
    }

    private Symbol CppLine()
    {
        TakeLineEscapedNewline();
        return FinishMarked(Symbol.Cpp, "cpp");
    }

    // --------------------------------------------------------------------------------------------------------
    // Comments
    // --------------------------------------------------------------------------------------------------------

    /// <summary>Distinguish between haddocks and plain comments by matching on the first non-whitespace character.</summary>
    private Symbol CommentType()
    {
        var i = 2u;
        while (Peek(i) == '-')
        {
            i++;
        }

        while (NotEof())
        {
            var c = Peek(i++);
            if (c == '|' || c == '^')
            {
                return Symbol.Haddock;
            }
            else if (!IsSpaceChar(c))
            {
                break;
            }
        }

        return Symbol.Comment;
    }

    /// <summary>
    /// Inline comments extend over all consecutive lines that start with comments.
    /// Could be improved by requiring equal indent.
    /// </summary>
    private Symbol InlineComment()
    {
        var sym = CommentType();
        do
        {
            TakeLine();
            Mark("inline comment");
            Advance();
            ResetLookahead();
        }
        while (LineCommentHerald());
        return sym;
    }

    private uint ConsumeBlockComment(uint col)
    {
        var level = 0u;
        for (; ; )
        {
            if (IsEof())
            {
                return col;
            }

            col++;
            switch (_lexer.Lookahead)
            {
                case '{':
                    Advance();
                    if (_lexer.Lookahead == '-')
                    {
                        Advance();
                        col++;
                        level++;
                    }

                    break;
                case '-':
                    Advance();
                    if (_lexer.Lookahead == '}')
                    {
                        Advance();
                        col++;
                        if (level == 0)
                        {
                            return col;
                        }

                        level--;
                    }

                    break;
                case '\n':
                case '\r':
                case '\f':
                    Advance();
                    col = 0;
                    break;
                case '\t':
                    Advance();
                    col += 7;
                    break;
                default:
                    Advance();
                    break;
            }
        }
    }

    /// <summary>
    /// Since {- -} comments can be nested arbitrarily, this has to keep track of how many have been opened, so that the
    /// outermost comment isn't closed prematurely.
    /// </summary>
    private Symbol BlockComment()
    {
        var sym = CommentType();
        ConsumeBlockComment(_lookaheadSize);
        return FinishMarked(sym, "block_comment");
    }

    // --------------------------------------------------------------------------------------------------------
    // Pragma
    // --------------------------------------------------------------------------------------------------------

    private bool ConsumePragma()
    {
        if (Seq("{-#"))
        {
            while (!Seq("#-}") && NotEof())
            {
                ResetLookahead();
                AdvanceOver(0);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Since pragmas can occur anywhere, like comments, but contrarily determine indentation when occurring at the
    /// beginning of a line in layouts, this sets <c>NResume</c> to continue newline processing with the indent of the
    /// pragma.
    ///
    /// If the pragma is followed by newline, this only ensures that no semicolon is emitted (since this rule is run
    /// before <c>semicolon</c> and <c>NResume</c> restarts lookahead).
    ///
    /// Otherwise it ensures that the following token is treated as a layout element with the correct indent.
    /// </summary>
    private Symbol Pragma()
    {
        if (ConsumePragma())
        {
            Mark("pragma");
            if (_newline.State != NewlineState.NInactive)
            {
                _newline.State = NewlineState.NResume;
            }

            return Finish(Symbol.Pragma, "newline");
        }

        return Symbol.Fail;
    }

    // --------------------------------------------------------------------------------------------------------
    // Quasiquote
    // --------------------------------------------------------------------------------------------------------

    private Symbol QqBody()
    {
        for (; ; )
        {
            if (IsEof())
            {
                return Finish(Symbol.QqBody, "qq_body");
            }
            else if (_lexer.Lookahead == 0x27e7)
            {
                return FinishMarked(Symbol.QqBody, "qq_body");
            }
            else if (_lexer.Lookahead == '|')
            {
                Mark("qq_body");
                Advance();
                if (_lexer.Lookahead == ']')
                {
                    return Finish(Symbol.QqBody, "qq_body");
                }
            }
            else
            {
                Advance();
            }
        }
    }

    // --------------------------------------------------------------------------------------------------------
    // Semicolon
    // --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// When encountering explicit semicolons, we want to ensure that a subsequent newline doesn't trigger a layout
    /// semicolon, so we set <c>skip_semi</c>.
    /// If the next symbol is not a newline (and not another semicolon), the scanner will immediate end up in
    /// <c>resolve_semicolon</c>, matching the condition, where we unset the flag to avoid a mid-line semicolon from
    /// influencing an unrelated newline.
    /// </summary>
    private Symbol ExplicitSemicolon()
    {
        if (Valid(Symbol.Semicolon) && !_newline.SkipSemi)
        {
            _newline.SkipSemi = true;
            return UpdateState("explicit semicolon enable");
        }

        return Symbol.Fail;
    }

    private Symbol ResolveSemicolon(Lexed next)
    {
        if (_newline.SkipSemi)
        {
            switch (next)
            {
                case Lexed.LLineComment:
                case Lexed.LBlockComment:
                case Lexed.LPragma:
                case Lexed.LSemi:
                    break;
                default:
                    _newline.SkipSemi = false;
                    return UpdateState("explicit semicolon disable");
            }
        }

        return Symbol.Fail;
    }

    /// <summary>
    /// Generate a layout semicolon after a newline if the indent is less or equal to the current layout's indent,
    /// unless:
    ///
    /// - The current context doesn't use layout semicolons, which is the case for explicit brace layouts, tuple
    ///   expressions, the module header and multi-way if layouts.
    ///
    /// - <c>no_semi</c> was set because newline lookahead found an explicit semicolon in the next line, or this
    ///   function was executed before for the same newline.
    ///
    /// - <c>skip_semi</c> was set because the previous line ended with an explicit semicolon.
    /// </summary>
    private Symbol Semicolon()
    {
        if (IsSemicolonContext() && !(_newline.NoSemi || _newline.SkipSemi) && IndentLesseq(_newline.Indent))
        {
            _newline.NoSemi = true;
            return Finish(Symbol.Semicolon, "newline");
        }
        else
        {
            return Symbol.Fail;
        }
    }

    // --------------------------------------------------------------------------------------------------------
    // High-level `Lexed` dispatch
    // --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Process a <c>Lexed</c> token if it results in a layout end or an extra.
    ///
    /// This is called by <c>newline_post</c> before marking, so the actions must not fail after advancing.
    /// </summary>
    private Symbol ProcessTokenSafe(Lexed next)
    {
        switch (next)
        {
            case Lexed.LWhere:
                return EndLayoutWhere();
            case Lexed.LIn:
                return EndLayoutIn();
            case Lexed.LThen:
            case Lexed.LElse:
                return EndLayout("then/else");
            case Lexed.LDeriving:
                return EndLayoutDeriving();
            case Lexed.LBar:
                if (!Valid(Symbol.Bar))
                {
                    return EndLayout("bar");
                }

                break;
            case Lexed.LPragma:
                return Pragma();
            case Lexed.LBlockComment:
                return BlockComment();
            case Lexed.LLineComment:
                return InlineComment();
            case Lexed.LCppElse:
                return CppElse(true);
            case Lexed.LCpp:
                return CppLine();
            case Lexed.LSymop:
            case Lexed.LTick:
            case Lexed.LHash:
                return EndLayoutInfix();
            case Lexed.LUnboxedClose:
                if (TokenEndLayoutTexp() is not Symbol.Fail and var res)
                {
                    return res;
                }

                return EndLayoutInfix();
            case Lexed.LArrow:
                if (!Valid(Symbol.Arrow))
                {
                    return TokenEndLayoutTexp();
                }

                break;
            case Lexed.LTexpCloser:
                return TokenEndLayoutTexp();
            case Lexed.LQuoteClose:
                return EndLayout("quote bracket");
            default:
                break;
        }

        return Symbol.Fail;
    }

    /// <summary>Process a <c>Lexed</c> token if it results in a symbolic operator.</summary>
    private Symbol ProcessTokenSymop(bool whitespace, Lexed next)
    {
        Symbol res;
        switch (next)
        {
            case Lexed.LDotDot:
                if ((res = FinishIfValid(Symbol.Dotdot, "symop")) != Symbol.Fail)
                {
                    return res;
                }

                return TightOp(whitespace, Symbol.QualDot);
            case Lexed.LDotId:
                if ((res = FinishIfValid(whitespace ? Symbol.PrefixDot : Symbol.TightDot, "symop")) != Symbol.Fail)
                {
                    return res;
                }

                return TightOp(whitespace, Symbol.QualDot);
            case Lexed.LDotSymop:
                return TightOrVarsym(whitespace, Symbol.QualDot);
            case Lexed.LDotOpen:
                return PrefixOrVarsym(whitespace, Symbol.PrefixDot);
            case Lexed.LBang:
                return InfixOrVarsym(whitespace, Symbol.PrefixBang, Symbol.TightBang);
            case Lexed.LTilde:
                return InfixOrVarsym(whitespace, Symbol.PrefixTilde, Symbol.TightTilde);
            case Lexed.LAt:
                return InfixOrVarsym(whitespace, Symbol.PrefixAt, Symbol.TightAt);
            case Lexed.LPercent:
                return PrefixOrVarsym(whitespace, Symbol.PrefixPercent);
            case Lexed.LSymop:
                if (Char0(':'))
                {
                    return FinishSymop(Symbol.Consym);
                }
                else
                {
                    return FinishSymop(Symbol.Varsym);
                }

            // The following are handled here despite not being purely symop tokens because `process_token_symop` is
            // executed last, and these handlers all have potentially quite far lookahead and can fail.
            case Lexed.LSymopSpecial:
                if ((res = LeftSectionOp(SymopLookahead())) != Symbol.Fail)
                {
                    return res;
                }

                if (Valid(Symbol.Minus) && MatchSymop("-"))
                {
                    return Finish(Symbol.Minus, "symop");
                }

                break;
            case Lexed.LUnboxedClose:
            case Lexed.LHash:
                return LeftSectionOp(SymopLookahead());
            case Lexed.LTick:
                return LeftSectionTicked();
            case Lexed.LUpper:
                if (Valid(Symbol.QualifiedOp) || Valid(Symbol.LeftSectionOp))
                {
                    if ((res = QualifiedOp()) != Symbol.Fail)
                    {
                        return res;
                    }
                }

                break;
            default:
                break;
        }

        return Symbol.Fail;
    }

    private Symbol ProcessTokenSplice(Lexed next)
    {
        switch (next)
        {
            case Lexed.LDollar:
                return FinishIfValid(Symbol.Splice, "symop");
            default:
                break;
        }

        return Symbol.Fail;
    }

    /// <summary>Process a <c>Lexed</c> token for an interior position.</summary>
    private Symbol ProcessTokenInterior(Lexed next)
    {
        Symbol res;
        switch (next)
        {
            case Lexed.LBraceClose:
                if ((res = EndLayoutBrace()) != Symbol.Fail)
                {
                    return res;
                }

                return TokenEndLayoutTexp();

            // Skip layout start
            case Lexed.LModule:
                return Symbol.Fail;
            case Lexed.LSemi:
                return ExplicitSemicolon();
            case Lexed.LBracketOpen:
                return Finish(Symbol.QqStart, "qq_start");
            default:
                break;
        }

        if ((res = ProcessTokenSafe(next)) != Symbol.Fail)
        {
            return res;
        }

        return StartLayoutInterior(next);
    }

    /// <summary>Process a <c>Lexed</c> token to initialize the context stack.</summary>
    private Symbol ProcessTokenInit(uint indent, Lexed next)
    {
        switch (next)
        {
            case Lexed.LModule:
                PushContext(ContextSort.ModuleHeader, 0);
                return UpdateState("init");
            case Lexed.LBraceOpen:
                AdvanceOver(0);
                Mark("init brace");
                PushContext(ContextSort.Braces, indent);
                return Finish(Symbol.StartExplicit, "init");
            default:
                PushContext(ContextSort.DeclLayout, indent);
                return Finish(Symbol.Start, "init");
        }
    }

    // --------------------------------------------------------------------------------------------------------
    // Newline actions
    // --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>NoSpace</c> + <c>newline_init()</c> means that we're at the very beginning of the file, where we start in
    /// <c>NResume</c> mode without a newline character that can tell us where we are.
    /// </summary>
    private Symbol NewlineExtras(Space space)
    {
        var bol = space == Space.BOL || (space == Space.NoSpace && NewlineInit());
        var next = LexExtras(bol);
        return ProcessTokenSafe(next);
    }

    // Don't finish newline processing before pragmas – they are indicators of layout indent, but since they are extras,
    // they cannot consume a semicolon, so when there's a pragma on a line of its own, we would get two semicolons if we
    // finished here.
    // It's guaranteed that the newline state was committed at least once because `newline_lookahead` sets `unsafe` when
    // finding a pragma.
    private Symbol NewlineProcess()
    {
        var indent = _newline.Indent;
        var end = _newline.End;
        Symbol res;
        if ((res = EndLayoutIndent()) != Symbol.Fail)
        {
            return res;
        }

        if ((res = ProcessTokenSafe(end)) != Symbol.Fail)
        {
            return res;
        }

        var space = SkipWhitespace();
        Mark("newline_post");
        if (_newline.Unsafe)
        {
            if ((res = NewlineExtras(space)) != Symbol.Fail)
            {
                return res;
            }
        }

        if (!_newline.Eof)
        {
            if ((res = StartLayoutNewline()) != Symbol.Fail)
            {
                return res;
            }
        }

        // TODO it is only necessary to run this late because of very few situations, like nondecreasing indent.
        // But it has the consequence that whitespace is included in the parent in nested layouts.
        // Maybe there's a way to run it before and after `start_layout_newline` with conditions.
        if ((res = Semicolon()) != Symbol.Fail)
        {
            return res;
        }

        ResetNewline();
        if (Uninitialized())
        {
            if ((res = ProcessTokenInit(indent, end)) != Symbol.Fail)
            {
                return res;
            }
        }
        else
        {
            if ((res = ProcessTokenSymop(true, end)) != Symbol.Fail)
            {
                return res;
            }

            if ((res = ProcessTokenSplice(end)) != Symbol.Fail)
            {
                return res;
            }
        }

        return UpdateState("newline final");
    }

    private Symbol NewlinePost()
    {
        var res = NewlineProcess();
        if (NewlineInit())
        {
            _newline.State = NewlineState.NProcess;
        }

        return res;
    }

    /// <summary>
    /// Repeatedly lex lookahead until encountering something that is neither a comment nor CPP, skipping whitespace
    /// and newlines in between.
    /// </summary>
    private void NewlineLookahead(ref Newline newline)
    {
        for (; ; )
        {
            // Using `peek0` to look for whitespace requires the lookahead buffer to have been reset immediately before
            // this statement – so before the call to this function or at the end of the for loop body.
            // The reason this isn't using `lexer->lookahead` is that the function may be called at an interior
            // position, to skip extras.
            switch (Peek0())
            {
                case '\n':
                case '\r':
                case '\f':
                    SkipOver(0);
                    newline.Indent = 0;
                    break;
                case '\t':
                    SkipOver(0);
                    newline.Indent += 8;
                    break;
                default:
                    if (IsSpaceChar(Peek0()))
                    {
                        SkipOver(0);
                        newline.Indent++;
                        break;
                    }

                    newline.End = Lex(newline.Indent == 0);

                    // Newlines without extras are only safe if `lex` didn't advance the lexer over non-whitespace.
                    newline.Unsafe |= !NoLookahead();
                    switch (newline.End)
                    {
                        case Lexed.LEof:
                            newline.Indent = 0;
                            newline.Eof = true;
                            return;

                        // If/then blocks can have semicolons, but don't have a layout.
                        // Allowing layout semicolons costs 100kB.
                        case Lexed.LThen:
                        case Lexed.LElse:
                        case Lexed.LSemi:
                            newline.NoSemi = true;
                            return;
                        case Lexed.LBlockComment:
                            newline.Indent = ConsumeBlockComment(newline.Indent + 2);
                            break;
                        case Lexed.LLineComment:
                            newline.Indent = 0;
                            TakeLine();
                            break;
                        case Lexed.LCppElse:
                            CppElse(false);
                            TakeLineEscapedNewline();
                            break;
                        case Lexed.LCpp:
                            TakeLineEscapedNewline();
                            break;
                        default:
                            return;
                    }

                    break;
            }

            ResetLookahead();
        }
    }

    /// <summary>
    /// Perform newline lookahead, then either finish the run if the position was advanced into the next token, or
    /// directly start newline processing if not.
    /// </summary>
    private Symbol NewlineStart()
    {
        _newline.State = NewlineState.NInit;
        NewlineLookahead(ref _newline);
        if (_newline.Unsafe)
        {
            return UpdateState("newline lookahead");
        }
        else
        {
            return NewlinePost();
        }
    }

    /// <summary>Perform newline lookahead with preset indent, used at the beginning of a file and after pragmas.</summary>
    private Symbol NewlineResume()
    {
        var indent = _newline.Indent;

        // Skip space between the pragma end and the next token, which might be the first real token (or another pragma
        // or comment, or newline).
        // We don't want to count the space as indent.
        SkipSpace();
        ResetNewline();
        _newline.Indent = indent;
        return NewlineStart();
    }

    // --------------------------------------------------------------------------------------------------------
    // Constraints
    // --------------------------------------------------------------------------------------------------------

    /// <summary>Result of constraint lookahead.</summary>
    private enum CtrResult
    {
        // Continue searching
        CtrUndecided,

        // Clear evidence found that no context or infix class is ahead.
        CtrImpossible,

        // The context arrow `=>` was found.
        CtrArrowFound,

        // An infix operator was found.
        CtrInfixFound,

        // An `=` was found.
        CtrEqualsFound,

        // A `|` was found.
        CtrBarFound,
    }

    /// <summary>Constraint lookahead state.</summary>
    private struct CtrState
    {
        // The amount of characters to skip after an iteration.
        // For example, after lexing a `conid` the next token can be lexed at the end of the identifier.
        public uint Reset;

        // The number of nested brackets.
        // When this is nonzero, end tokens are not treated as pertaining to the current expression.
        public uint Brackets;

        // A context arrow was found.
        public bool Context;

        // An infix operator was found.
        public bool Infix;
        public bool DataInfix;
        public bool TypeInstance;
    }

    /// <summary>Increment the bracket count.</summary>
    private static CtrResult CtrBracketOpen(ref CtrState state)
    {
        state.Brackets++;
        state.Reset = 1;
        return CtrResult.CtrUndecided;
    }

    /// <summary>
    /// Decrement the bracket count.
    /// If the count was zero already, parsing started inside of brackets that are closed here, so lookahead is
    /// terminated.
    /// </summary>
    private static CtrResult CtrBracketClose(ref CtrState state)
    {
        if (state.Brackets == 0)
        {
            return CtrResult.CtrImpossible;
        }

        state.Brackets--;
        state.Reset = 1;
        return CtrResult.CtrUndecided;
    }

    /// <summary>If the given token is ahead, terminate lookahead unsuccessfully.</summary>
    private CtrResult CtrStopOnToken(string target) => Token(target) ? CtrResult.CtrImpossible : CtrResult.CtrUndecided;

    /// <summary>
    /// Check if the lexed token is <c>=></c> or an infix operator.
    ///
    /// This is performed only when the current position is not in a bracketed expression, i.e. at top level relative to
    /// the initial lexer position.
    /// Otherwise the token belongs to a later, nested expression.
    ///
    /// Certain tokens are proof that no context can start at the current position, like <c>::</c> or <c>forall</c>, so
    /// lookahead is terminated.
    /// </summary>
    private CtrResult CtrTop(Lexed next)
    {
        switch (next)
        {
            case Lexed.LCArrow:
                return CtrResult.CtrArrowFound;
            case Lexed.LSymop:
            case Lexed.LSymopSpecial:
            case Lexed.LTilde:
            case Lexed.LTick:
                return CtrResult.CtrInfixFound;
            case Lexed.LBar:
                return CtrResult.CtrBarFound;
            case Lexed.LArrow:
            case Lexed.LWhere:
            case Lexed.LDotDot:
            case Lexed.LSemi:
                break;
            case Lexed.LTexpCloser:
                switch (Peek0())
                {
                    case '=':
                        return CtrResult.CtrEqualsFound;
                    default:
                        break;
                }

                break;
            default:
                switch (Peek0())
                {
                    // Symop is processed in `ctr_lookahead_step`, so `=` and `::` can not be a prefix
                    case '=':
                        return CtrResult.CtrEqualsFound;
                    case 0x2200: // ∀
                        break;
                    case ':':
                        if (Char1(':'))
                        {
                            break;
                        }

                        return CtrResult.CtrUndecided;
                    case 'f':
                        if (CtrStopOnToken("forall") is not CtrResult.CtrUndecided and var res)
                        {
                            return res;
                        }

                        return CtrStopOnToken("family");
                    case 'i':
                        return CtrStopOnToken("instance");
                    default:
                        return CtrResult.CtrUndecided;
                }

                break;
        }

        return CtrResult.CtrImpossible;
    }

    /// <summary>
    /// Process a lexed token for constraint lookahead:
    /// - Update bracket nesting count
    /// - Advance over pragmas, strings, chars and conids
    /// - Set the reset index for certain tokens
    ///
    /// If the token wasn't identified to be irrelevant for the lookahead result, and the current bracket nesting level
    /// is zero, call <c>ctr_top</c>.
    /// </summary>
    private CtrResult CtrLookaheadStep(ref CtrState state, Lexed next)
    {
        state.Reset = 1;
        switch (next)
        {
            case Lexed.LBraceClose:
                return CtrBracketClose(ref state);
            case Lexed.LUnboxedClose:
                if (CtrBracketClose(ref state) is not CtrResult.CtrUndecided and var res)
                {
                    return res;
                }

                state.Reset = 2;
                return CtrResult.CtrUndecided;
            case Lexed.LBraceOpen:
                return CtrBracketOpen(ref state);
            case Lexed.LSymopSpecial:
            case Lexed.LSymop:
                state.Reset = SymopLookahead();
                break;
            case Lexed.LUpper:
                state.Reset = Conid();
                return CtrResult.CtrUndecided;
            case Lexed.LDotId:
                return CtrResult.CtrUndecided;
            case Lexed.LPragma:
                if (ConsumePragma())
                {
                    state.Reset = 3;
                }

                return CtrResult.CtrUndecided;
            case Lexed.LTexpCloser:
            case Lexed.LNothing:
                switch (Peek0())
                {
                    case ')':
                    case ']':
                        return CtrBracketClose(ref state);
                    case '(':
                    case '[':
                        return CtrBracketOpen(ref state);
                    case '"':
                        state.Reset = TakeStringLiteral();
                        return CtrResult.CtrUndecided;
                    case '\'':
                        state.Reset = TakeCharLiteral();
                        return CtrResult.CtrUndecided;
                    default:
                        if (VaridStartChar(Peek0()))
                        {
                            state.Reset = AdvanceWhile(1, IsIdChar);
                        }

                        break;
                }

                // The C code falls through to the default case
                break;
            default:
                break;
        }

        if (state.Brackets != 0)
        {
            return CtrResult.CtrUndecided;
        }

        return CtrTop(next);
    }

    /// <summary>
    /// Main loop for context lookahead.
    ///
    /// Perform newline lookahead and terminate if the end of the current layout element is encountered.
    /// Otherwise use the new end token to detect a context arrow or infix operator.
    /// If no termination criterion is fulfilled, reset lookahead and repeat.
    ///
    /// Newline lookahead skips over extras.
    ///
    /// A context arrow is always a termination criterion; an infix operator only if CONTEXT isn't valid.
    /// </summary>
    private Symbol ConstraintLookahead()
    {
        var state = new CtrState { Reset = 0 };
        var done = false;
        while (!done && NotEof())
        {
            // Setting indent to 99999 only to not trigger the following termination condition when no newline was
            // advanced over
            var newline = new Newline { State = NewlineState.NInactive, Indent = 99999 };
            NewlineLookahead(ref newline);
            if (newline.Indent <= CurrentIndent() && CurrentContext() != ContextSort.Braces)
            {
                break;
            }

            var result = CtrLookaheadStep(ref state, newline.End);
            switch (result)
            {
                case CtrResult.CtrArrowFound:
                    state.Context = true;
                    done = true;
                    break;
                case CtrResult.CtrInfixFound:
                    if (Char0(':') || Char0('`'))
                    {
                        state.DataInfix = true;
                    }

                    state.Infix = true;

                    // Context has precedence, e.g. `instance a + a => A` finds `+` first and would treat that as the
                    // class name of the head, then failing on the right operand.
                    done = !Valid(Symbol.Context);
                    break;
                case CtrResult.CtrEqualsFound:
                    done = !Valid(Symbol.TypeInstance);
                    state.TypeInstance = true;
                    break;
                case CtrResult.CtrBarFound:
                    done = true;
                    state.TypeInstance = false;
                    break;
                case CtrResult.CtrImpossible:
                    done = true;
                    break;
                case CtrResult.CtrUndecided:
                    break;
            }

            ResetLookaheadTo(state.Reset);
            state.Reset = 0;
        }

        Symbol res;
        if (state.Context && (res = FinishIfValid(Symbol.Context, "ctr")) != Symbol.Fail)
        {
            return res;
        }

        if (state.Infix && (res = FinishIfValid(Symbol.Infix, "ctr")) != Symbol.Fail)
        {
            return res;
        }

        if (state.DataInfix && (res = FinishIfValid(Symbol.DataInfix, "ctr")) != Symbol.Fail)
        {
            return res;
        }

        if (state.TypeInstance && (res = FinishIfValid(Symbol.TypeInstance, "ctr")) != Symbol.Fail)
        {
            return res;
        }

        return Symbol.Fail;
    }

    // --------------------------------------------------------------------------------------------------------
    // Actions that are executed for interior positions
    // --------------------------------------------------------------------------------------------------------

    private Symbol ProcessTokenConstraint()
    {
        if (Valid(Symbol.Context) || Valid(Symbol.Infix) || Valid(Symbol.DataInfix) || Valid(Symbol.TypeInstance))
        {
            return ConstraintLookahead();
        }

        return Symbol.Fail;
    }

    private Symbol Interior(bool whitespace)
    {
        var next = Lex(false);
        Symbol res;
        if ((res = ResolveSemicolon(next)) != Symbol.Fail)
        {
            return res;
        }

        if ((res = ProcessTokenInterior(next)) != Symbol.Fail)
        {
            return res;
        }

        if ((res = ProcessTokenSymop(whitespace, next)) != Symbol.Fail)
        {
            return res;
        }

        if ((res = ProcessTokenConstraint()) != Symbol.Fail)
        {
            return res;
        }

        if ((res = ProcessTokenSplice(next)) != Symbol.Fail)
        {
            return res;
        }

        return Symbol.Fail;
    }

    // --------------------------------------------------------------------------------------------------------
    // Initial actions
    // --------------------------------------------------------------------------------------------------------

    /// <summary>These are conditioned only on symbols and don't advance, except for <c>qq_body</c>, which cannot fail.</summary>
    private Symbol PreWsCommands()
    {
        Symbol res;
        if ((res = TexpContext()) != Symbol.Fail)
        {
            return res;
        }

        if ((res = StartBrace()) != Symbol.Fail)
        {
            return res;
        }

        if ((res = EndBrace()) != Symbol.Fail)
        {
            return res;
        }

        // Leading whitespace must be included in the node.
        if (Valid(Symbol.QqBody))
        {
            return QqBody();
        }

        if (NewlineActive())
        {
            if ((res = NewlinePost()) != Symbol.Fail)
            {
                return res;
            }
        }
        else if (_newline.State == NewlineState.NResume)
        {
            if ((res = NewlineResume()) != Symbol.Fail)
            {
                return res;
            }
        }

        return Symbol.Fail;
    }

    private Symbol ScanMain()
    {
        Mark("main");
        if (PreWsCommands() is not Symbol.Fail and var res)
        {
            return res;
        }

        var whitespace = SkipSpace();
        if (IsNewline(_lexer.Lookahead))
        {
            return NewlineStart();
        }
        else if (NotEof())
        {
            return Interior(whitespace);
        }

        return Symbol.Fail;
    }

    private bool ProcessResult(Symbol result)
    {
        if (result == Symbol.Fail && IsEof() && NoLookahead())
        {
            Mark("eof whitespace");

            // Inlined `end_layout` because of perf glitch
            if (Valid(Symbol.End))
            {
                result = EndLayoutUnchecked("eof");
            }
            else if (Valid(Symbol.Semicolon))
            {
                result = Finish(Symbol.Semicolon, "eof");
            }
            else
            {
                result = ForceEndContext();
            }
        }

        return SetResultSymbol(result);
    }

    private bool Scan()
    {
        if (AfterError())
        {
            return false;
        }

        var result = ScanMain();
        return ProcessResult(result);
    }

    // --------------------------------------------------------------------------------------------------------
    // API
    // --------------------------------------------------------------------------------------------------------

    // The size of the C struct Persist: the number of contexts (unsigned) and the Newline struct (three 4-byte fields
    // and four bools, without padding). A Context is two 4-byte fields.
    private const int PersistSize = 20;
    private const int ContextSize = 8;

    private readonly Func<bool> _isSymop;

    /// <summary>
    /// Creates the scanner, like <c>tree_sitter_haskell_external_scanner_create</c>: no contexts and an inactive
    /// newline state (the parser deserializes the initial state before the first scan).
    /// </summary>
    public HaskellScanner()
    {
        _isSymop = IsSymop;
    }

    /// <inheritdoc />
    /// <remarks>Main logic entry point.</remarks>
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        // env_new
        _lexer = lexer;
        validSymbols.CopyTo(_symbols);
        _symop = 0;
        return Scan();
    }

    /// <inheritdoc />
    /// <remarks>The bytes of the C structs Persist and Context, little-endian.</remarks>
    public int Serialize(Span<byte> buffer)
    {
        var contextsSize = (int)_contextsSize * ContextSize;
        var toCopy = PersistSize + contextsSize;
        if (toCopy > ExternalScanner.StateCapacity)
        {
            return 0;
        }

        WriteUInt32(buffer, 0, _contextsSize);
        WriteUInt32(buffer, 4, (uint)_newline.State);
        WriteUInt32(buffer, 8, (uint)_newline.End);
        WriteUInt32(buffer, 12, _newline.Indent);
        buffer[16] = _newline.Eof ? (byte)1 : (byte)0;
        buffer[17] = _newline.NoSemi ? (byte)1 : (byte)0;
        buffer[18] = _newline.SkipSemi ? (byte)1 : (byte)0;
        buffer[19] = _newline.Unsafe ? (byte)1 : (byte)0;
        for (var i = 0; i < (int)_contextsSize; i++)
        {
            WriteUInt32(buffer, PersistSize + (i * ContextSize), (uint)_contexts[i].Sort);
            WriteUInt32(buffer, PersistSize + (i * ContextSize) + 4, _contexts[i].Indent);
        }

        return toCopy;
    }

    /// <inheritdoc />
    public void Deserialize(ReadOnlySpan<byte> state)
    {
        uint contexts;
        if (state.Length > 0)
        {
            contexts = ReadUInt32(state, 0);
            _newline = new Newline
            {
                State = (NewlineState)ReadUInt32(state, 4),
                End = (Lexed)ReadUInt32(state, 8),
                Indent = ReadUInt32(state, 12),
                Eof = state[16] != 0,
                NoSemi = state[17] != 0,
                SkipSemi = state[18] != 0,
                Unsafe = state[19] != 0,
            };
        }
        else
        {
            contexts = 0;
            _newline = new Newline { State = NewlineState.NResume };
        }

        if (_contexts.Length < contexts)
        {
            Array.Resize(ref _contexts, (int)contexts);
        }

        _contextsSize = contexts;
        if (state.Length > 0)
        {
            for (var i = 0; i < (int)contexts; i++)
            {
                _contexts[i] = new Context
                {
                    Sort = (ContextSort)ReadUInt32(state, PersistSize + (i * ContextSize)),
                    Indent = ReadUInt32(state, PersistSize + (i * ContextSize) + 4),
                };
            }
        }

        _lookaheadSize = 0;
        _lookaheadOffset = 0;
    }

    private static void WriteUInt32(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset) =>
        buffer[offset] | ((uint)buffer[offset + 1] << 8) | ((uint)buffer[offset + 2] << 16) | ((uint)buffer[offset + 3] << 24);
}
