// The tree-sitter grammar DSL, evaluated by Jint before a grammar.js file.
// Each function returns a plain object with the shape of the rules of a grammar.json file.
(function (global) {
  'use strict';

  class GrammarSymbol {
    constructor(name) {
      this.type = 'SYMBOL';
      this.name = name;
    }
  }

  class RustRegex {
    constructor(value) {
      this.value = value;
    }
  }

  function normalize(value) {
    if (typeof value === 'undefined') {
      throw new Error('Undefined symbol');
    }
    if (value === null) {
      throw new TypeError('Invalid rule: null');
    }
    if (typeof value === 'string' || value instanceof String) {
      return { type: 'STRING', value: String(value) };
    }
    if (value instanceof RegExp) {
      return value.flags
        ? { type: 'PATTERN', value: value.source, flags: value.flags }
        : { type: 'PATTERN', value: value.source };
    }
    if (value instanceof RustRegex) {
      return { type: 'PATTERN', value: value.value };
    }
    if (value instanceof ReferenceError) {
      throw value;
    }
    if (typeof value.type === 'string') {
      return value;
    }
    throw new TypeError('Invalid rule: ' + value);
  }

  function checkArguments(args, ruleCount, callerName, suffix, argType) {
    suffix = suffix || '';
    argType = argType || 'rule';
    // Allow .map() usage, where the extra arguments are the index and the array.
    const isMapCall = ruleCount === 3 && typeof args[1] === 'number' && Array.isArray(args[2]);
    if (ruleCount > 1 && !isMapCall) {
      throw new Error(
        'The `' + callerName + '` function only takes one ' + argType + ' argument' + suffix + '.\n' +
        'You passed in multiple ' + argType + 's. Did you mean to call `seq`?\n');
    }
  }

  function checkPrecedence(value) {
    if (value === null || value === undefined) {
      throw new Error('Missing precedence value');
    }
  }

  function alias(rule, value) {
    const result = { type: 'ALIAS', content: normalize(rule), named: false, value: null };
    if (typeof value === 'string' || value instanceof String) {
      result.value = String(value);
      return result;
    }
    if (value instanceof ReferenceError) {
      result.named = true;
      result.value = value.symbol.name;
      return result;
    }
    if (value && value.type === 'SYMBOL' && typeof value.name === 'string') {
      result.named = true;
      result.value = value.name;
      return result;
    }
    throw new Error('Invalid alias value ' + value);
  }

  function blank() {
    return { type: 'BLANK' };
  }

  function eof() {
    return { type: 'EOF' };
  }

  function field(name, rule) {
    if (typeof name !== 'string' || !/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(name)) {
      throw new Error("Invalid field name '" + name + "': field names must start with a letter or underscore, followed by letters, digits, or underscores");
    }
    return { type: 'FIELD', name: name, content: normalize(rule) };
  }

  function choice(...elements) {
    return { type: 'CHOICE', members: elements.map(e => normalize(e)) };
  }

  function optional(value) {
    checkArguments(arguments, arguments.length, 'optional');
    return choice(value, blank());
  }

  function prec(number, rule) {
    checkPrecedence(number);
    checkArguments(arguments, arguments.length - 1, 'prec', ' and a precedence argument');
    return { type: 'PREC', value: number, content: normalize(rule) };
  }

  prec.left = function (number, rule) {
    if (rule === null || rule === undefined) {
      rule = number;
      number = 0;
    }
    checkPrecedence(number);
    checkArguments(arguments, arguments.length - 1, 'prec.left', ' and an optional precedence argument');
    return { type: 'PREC_LEFT', value: number, content: normalize(rule) };
  };

  prec.right = function (number, rule) {
    if (rule === null || rule === undefined) {
      rule = number;
      number = 0;
    }
    checkPrecedence(number);
    checkArguments(arguments, arguments.length - 1, 'prec.right', ' and an optional precedence argument');
    return { type: 'PREC_RIGHT', value: number, content: normalize(rule) };
  };

  prec.dynamic = function (number, rule) {
    checkPrecedence(number);
    checkArguments(arguments, arguments.length - 1, 'prec.dynamic', ' and a precedence argument');
    return { type: 'PREC_DYNAMIC', value: number, content: normalize(rule) };
  };

  function repeat(rule) {
    checkArguments(arguments, arguments.length, 'repeat');
    return { type: 'REPEAT', content: normalize(rule) };
  }

  function repeat1(rule) {
    checkArguments(arguments, arguments.length, 'repeat1');
    return { type: 'REPEAT1', content: normalize(rule) };
  }

  function seq(...elements) {
    return { type: 'SEQ', members: elements.map(e => normalize(e)) };
  }

  function reserved(wordset, rule) {
    if (typeof wordset !== 'string') {
      throw new Error('Invalid reserved word set name: ' + wordset);
    }
    return { type: 'RESERVED', content: normalize(rule), context_name: wordset };
  }

  function sym(name) {
    return new GrammarSymbol(name);
  }

  function token(value) {
    checkArguments(arguments, arguments.length, 'token', '', 'literal');
    return { type: 'TOKEN', content: normalize(value) };
  }

  token.immediate = function (value) {
    checkArguments(arguments, arguments.length, 'token.immediate', '', 'literal');
    return { type: 'IMMEDIATE_TOKEN', content: normalize(value) };
  };

  // The `$` object passed to the rule functions: any property is a symbol.
  function RuleBuilder(ruleMap) {
    return new Proxy({}, {
      get(_, propertyName) {
        if (typeof propertyName !== 'string') {
          return undefined;
        }
        const symbol = sym(propertyName);
        if (!ruleMap || Object.prototype.hasOwnProperty.call(ruleMap, propertyName)) {
          return symbol;
        }
        const error = new ReferenceError("Undefined symbol '" + propertyName + "'");
        error.symbol = symbol;
        return error;
      }
    });
  }

  function grammar(baseGrammar, options) {
    let inherits = undefined;

    if (!options) {
      options = baseGrammar;
      baseGrammar = {
        name: null,
        rules: {},
        extras: [normalize(/\s/)],
        conflicts: [],
        externals: [],
        inline: [],
        supertypes: [],
        precedences: [],
        reserved: {},
      };
    } else {
      baseGrammar = baseGrammar.grammar;
      inherits = baseGrammar.name;
    }

    let externals = baseGrammar.externals;
    if (options.externals) {
      if (typeof options.externals !== 'function') {
        throw new Error("Grammar's 'externals' property must be a function.");
      }
      const externalsRuleBuilder = RuleBuilder(null);
      const externalRules = options.externals.call(externalsRuleBuilder, externalsRuleBuilder, baseGrammar.externals);
      if (!Array.isArray(externalRules)) {
        throw new Error("Grammar's 'externals' property must return an array of rules.");
      }
      externals = externalRules.map(e => normalize(e));
    }

    const ruleMap = {};
    for (const key of Object.keys(options.rules || {})) {
      ruleMap[key] = true;
    }
    for (const key of Object.keys(baseGrammar.rules)) {
      ruleMap[key] = true;
    }
    for (const external of externals) {
      if (typeof external.name === 'string') {
        ruleMap[external.name] = true;
      }
    }

    const ruleBuilder = RuleBuilder(ruleMap);

    const name = options.name;
    if (typeof name !== 'string') {
      throw new Error("Grammar's 'name' property must be a string.");
    }
    if (!/^[a-zA-Z_]\w*$/.test(name)) {
      throw new Error("Grammar's 'name' property must not start with a digit and cannot contain non-word characters.");
    }

    const rules = Object.assign({}, baseGrammar.rules);
    if (options.rules) {
      if (typeof options.rules !== 'object') {
        throw new Error("Grammar's 'rules' property must be an object.");
      }
      for (const ruleName of Object.keys(options.rules)) {
        const ruleFn = options.rules[ruleName];
        if (typeof ruleFn !== 'function') {
          throw new Error("Grammar rules must all be functions. '" + ruleName + "' rule is not.");
        }
        const rule = ruleFn.call(ruleBuilder, ruleBuilder, baseGrammar.rules[ruleName]);
        if (rule === undefined) {
          throw new Error("Rule '" + ruleName + "' returned undefined.");
        }
        rules[ruleName] = normalize(rule);
      }
    }

    const reservedSets = Object.assign({}, baseGrammar.reserved);
    if (options.reserved) {
      if (typeof options.reserved !== 'object') {
        throw new Error("Grammar's 'reserved' property must be an object.");
      }
      for (const setName of Object.keys(options.reserved)) {
        const setFn = options.reserved[setName];
        if (typeof setFn !== 'function') {
          throw new Error("Grammar reserved word sets must all be functions. '" + setName + "' is not.");
        }
        const tokens = setFn.call(ruleBuilder, ruleBuilder, baseGrammar.reserved[setName]);
        if (!Array.isArray(tokens)) {
          throw new Error("Grammar's reserved word set functions must all return arrays of rules. '" + setName + "' does not.");
        }
        reservedSets[setName] = tokens.map(t => normalize(t));
      }
    }

    let extras = baseGrammar.extras.slice();
    if (options.extras) {
      if (typeof options.extras !== 'function') {
        throw new Error("Grammar's 'extras' property must be a function.");
      }
      extras = options.extras.call(ruleBuilder, ruleBuilder, baseGrammar.extras);
      if (!Array.isArray(extras)) {
        throw new Error("Grammar's 'extras' function must return an array.");
      }
      extras = extras.map(e => normalize(e));
    }

    let word = baseGrammar.word;
    if (options.word) {
      const wordRule = options.word.call(ruleBuilder, ruleBuilder);
      if (wordRule && wordRule.name === 'ReferenceError') {
        throw new Error("Grammar's 'word' property must be a valid named rule.");
      }
      if (!wordRule || wordRule.type !== 'SYMBOL' || typeof wordRule.name !== 'string') {
        throw new Error("Grammar's 'word' property must be a named rule.");
      }
      word = wordRule.name;
    }

    let conflicts = baseGrammar.conflicts;
    if (options.conflicts) {
      if (typeof options.conflicts !== 'function') {
        throw new Error("Grammar's 'conflicts' property must be a function.");
      }
      const baseConflictRules = baseGrammar.conflicts.map(conflict => conflict.map(sym));
      const conflictRules = options.conflicts.call(ruleBuilder, ruleBuilder, baseConflictRules);
      if (!Array.isArray(conflictRules)) {
        throw new Error("Grammar's conflicts must be an array of arrays of rules.");
      }
      conflicts = conflictRules.map(conflictSet => {
        if (!Array.isArray(conflictSet)) {
          throw new Error("Grammar's conflicts must be an array of arrays of rules.");
        }
        return conflictSet.map(symbol => {
          const rule = normalize(symbol);
          if (rule.type !== 'SYMBOL') {
            throw new Error("Grammar's conflicts must contain only named rules.");
          }
          return rule.name;
        });
      });
    }

    let inline = baseGrammar.inline;
    if (options.inline) {
      if (typeof options.inline !== 'function') {
        throw new Error("Grammar's 'inline' property must be a function.");
      }
      const baseInlineRules = baseGrammar.inline.map(sym);
      const inlineRules = options.inline.call(ruleBuilder, ruleBuilder, baseInlineRules);
      if (!Array.isArray(inlineRules)) {
        throw new Error("Grammar's inline must be an array of rules.");
      }
      inline = inlineRules.filter((symbol, index, self) => {
        if (symbol && symbol.name === 'ReferenceError') {
          console.log("Warning: inline rule '" + symbol.symbol.name + "' is not defined.");
          return false;
        }
        if (!symbol || symbol.type !== 'SYMBOL' || typeof symbol.name !== 'string') {
          throw new Error("Grammar's inline property must contain only named rules.");
        }
        if (self.findIndex(s => s && s.name === symbol.name) !== index) {
          console.log("Warning: duplicate inline rule '" + symbol.name + "'");
          return false;
        }
        return true;
      }).map(symbol => symbol.name);
    }

    let supertypes = baseGrammar.supertypes;
    if (options.supertypes) {
      if (typeof options.supertypes !== 'function') {
        throw new Error("Grammar's 'supertypes' property must be a function.");
      }
      const baseSupertypeRules = baseGrammar.supertypes.map(sym);
      const supertypeRules = options.supertypes.call(ruleBuilder, ruleBuilder, baseSupertypeRules);
      if (!Array.isArray(supertypeRules)) {
        throw new Error("Grammar's supertypes must be an array of rules.");
      }
      supertypes = supertypeRules.map(symbol => {
        if (symbol && symbol.name === 'ReferenceError') {
          throw new Error('Supertype rule `' + symbol.symbol.name + '` is not defined.');
        }
        if (!symbol || symbol.type !== 'SYMBOL' || typeof symbol.name !== 'string') {
          throw new Error("Grammar's supertypes property must contain only named rules.");
        }
        return symbol.name;
      });
    }

    let precedences = baseGrammar.precedences;
    if (options.precedences) {
      if (typeof options.precedences !== 'function') {
        throw new Error("Grammar's 'precedences' property must be a function");
      }
      precedences = options.precedences.call(ruleBuilder, ruleBuilder, baseGrammar.precedences);
      if (!Array.isArray(precedences)) {
        throw new Error("Grammar's precedences must be an array of arrays of rules.");
      }
      precedences = precedences.map(list => {
        if (!Array.isArray(list)) {
          throw new Error("Grammar's precedences must be an array of arrays of rules.");
        }
        return list.map(entry => {
          const rule = normalize(entry);
          if (rule.type !== 'STRING' && rule.type !== 'SYMBOL') {
            throw new Error("Grammar's precedences must contain only precedence names or named rules.");
          }
          return rule;
        });
      });
    }

    if (Object.keys(rules).length === 0) {
      throw new Error('Grammar must have at least one rule.');
    }

    return {
      grammar: {
        name,
        inherits,
        word,
        rules,
        extras,
        conflicts,
        precedences,
        externals,
        inline,
        supertypes,
        reserved: reservedSets,
      },
    };
  }

  global.alias = alias;
  global.blank = blank;
  global.eof = eof;
  global.choice = choice;
  global.optional = optional;
  global.prec = prec;
  global.repeat = repeat;
  global.repeat1 = repeat1;
  global.reserved = reserved;
  global.seq = seq;
  global.sym = sym;
  global.token = token;
  global.grammar = grammar;
  global.field = field;
  global.RustRegex = RustRegex;
})(globalThis);
