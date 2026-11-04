namespace TreeSpitter.Generation;

/// <summary>The type of a child: a symbol, or an alias. Port of <c>ChildType</c> in tree-sitter's node_types.rs.</summary>
internal readonly record struct ChildType(Symbol Symbol, Alias? Alias) : IComparable<ChildType>
{
    public int CompareTo(ChildType other)
    {
        // Normal(symbol) < Aliased(alias)
        if (Alias is null)
        {
            return other.Alias is null ? Symbol.CompareTo(other.Symbol) : -1;
        }

        return other.Alias is null ? 1 : Alias.CompareTo(other.Alias);
    }
}

/// <summary>The fields and children of a variable, the subset of tree-sitter's <c>VariableInfo</c> used by the tables.</summary>
internal sealed class VariableInfo
{
    public SortedDictionary<string, List<ChildType>> Fields { get; } = new(StringComparer.Ordinal);

    public List<ChildType> ChildTypes { get; } = [];

    public bool HasMultiStepProduction { get; set; }

    public VariableInfo Clone()
    {
        var clone = new VariableInfo { HasMultiStepProduction = HasMultiStepProduction };
        foreach (var (name, types) in Fields)
        {
            clone.Fields[name] = [.. types];
        }

        clone.ChildTypes.AddRange(ChildTypes);
        return clone;
    }

    public static List<VariableInfo> Compute(SyntaxGrammar syntaxGrammar, LexicalGrammar lexicalGrammar, AliasMap defaultAliases)
    {
        VariableType TypeOf(ChildType childType)
        {
            if (childType.Alias is { } alias)
            {
                return alias.Kind;
            }

            var symbol = childType.Symbol;
            if (syntaxGrammar.SupertypeSymbols.Contains(symbol))
            {
                return VariableType.Named;
            }

            if (syntaxGrammar.VariablesToInline.Contains(symbol))
            {
                return VariableType.Hidden;
            }

            return symbol.Kind switch
            {
                SymbolType.NonTerminal => syntaxGrammar.Variables[symbol.Index].Kind,
                SymbolType.Terminal => lexicalGrammar.Variables[symbol.Index].Kind,
                SymbolType.External => syntaxGrammar.ExternalTokens[symbol.Index].Kind,
                _ => VariableType.Hidden,
            };
        }

        bool IsVisible(ChildType t) => TypeOf(t) >= VariableType.Anonymous;

        var result = syntaxGrammar.Variables.Select(_ => new VariableInfo()).ToList();
        var didChange = true;
        while (didChange)
        {
            didChange = false;
            for (var i = 0; i < syntaxGrammar.Variables.Count; i++)
            {
                var info = result[i].Clone();
                foreach (var production in syntaxGrammar.Variables[i].Productions)
                {
                    if (production.Steps.Count > 1)
                    {
                        info.HasMultiStepProduction = true;
                    }

                    foreach (var step in production.Steps)
                    {
                        var childSymbol = step.Symbol;
                        var childType = step.Alias is { } alias
                            ? new ChildType(default, alias)
                            : defaultAliases.TryGetValue(childSymbol, out var defaultAlias)
                                ? new ChildType(default, defaultAlias)
                                : new ChildType(childSymbol, null);
                        var childIsHidden = !IsVisible(childType) && !syntaxGrammar.SupertypeSymbols.Contains(childSymbol);

                        didChange |= ExtendSorted(info.ChildTypes, [childType]);

                        if (step.FieldName is { } fieldName)
                        {
                            if (!info.Fields.TryGetValue(fieldName, out var fieldTypes))
                            {
                                fieldTypes = [];
                                info.Fields[fieldName] = fieldTypes;
                                didChange = true;
                            }

                            didChange |= ExtendSorted(fieldTypes, [childType]);
                            if (childIsHidden && childSymbol.IsNonTerminal)
                            {
                                didChange |= ExtendSorted(fieldTypes, result[childSymbol.Index].ChildTypes);
                            }
                        }

                        if (childIsHidden && childSymbol.IsNonTerminal)
                        {
                            var childInfo = result[childSymbol.Index];
                            if (childInfo.HasMultiStepProduction && !info.HasMultiStepProduction)
                            {
                                info.HasMultiStepProduction = true;
                                didChange = true;
                            }

                            foreach (var (childFieldName, childFieldTypes) in childInfo.Fields)
                            {
                                if (!info.Fields.TryGetValue(childFieldName, out var fieldTypes))
                                {
                                    fieldTypes = [];
                                    info.Fields[childFieldName] = fieldTypes;
                                    didChange = true;
                                }

                                didChange |= ExtendSorted(fieldTypes, childFieldTypes);
                            }

                            didChange |= ExtendSorted(info.ChildTypes, childInfo.ChildTypes);
                        }
                    }
                }

                result[i] = info;
            }
        }

        foreach (var info in result)
        {
            foreach (var types in info.Fields.Values)
            {
                types.RemoveAll(t => !IsVisible(t));
            }

            foreach (var name in info.Fields.Where(f => f.Value.Count == 0).Select(f => f.Key).ToList())
            {
                info.Fields.Remove(name);
            }
        }

        return result;
    }

    private static bool ExtendSorted(List<ChildType> list, IEnumerable<ChildType> values)
    {
        var changed = false;
        foreach (var value in values)
        {
            var index = list.BinarySearch(value);
            if (index < 0)
            {
                list.Insert(~index, value);
                changed = true;
            }
        }

        return changed;
    }
}
