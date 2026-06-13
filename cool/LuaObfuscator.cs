using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LuaObfuscator
{
    static class Rng
    {
        static readonly Random R = new Random();
        public static int Next(int a, int b) => R.Next(a, b);
        public static int Next(int b) => R.Next(b);
        public static double NextDouble() => R.NextDouble();
    }

    static class Names
    {
        static readonly HashSet<string> Used = new HashSet<string>();
        static readonly string V = "abcdefghijklmnopqrstuvwxyz";
        static readonly string VC = "aeiou";
        static readonly string CC = "bcdfghjklmnpqrstvwxyz";

        public static void Reset() { Used.Clear(); }

        public static string Gen(int min = 2, int max = 6)
        {
            for (int attempt = 0; attempt < 10000; attempt++)
            {
                var sb = new StringBuilder();
                int len = Rng.Next(min, max + 1);
                bool useUnderscore = Rng.Next(4) == 0;
                if (useUnderscore) sb.Append('_');
                for (int i = 0; i < len; i++)
                {
                    if (i % 2 == 0) sb.Append(CC[Rng.Next(CC.Length)]);
                    else sb.Append(VC[Rng.Next(VC.Length)]);
                }
                if (Rng.Next(3) == 0) sb.Append(Rng.Next(10));
                string n = sb.ToString();
                // avoid Lua keywords
                string[] kw = { "and","break","do","else","elseif","end","false","for",
                    "function","goto","if","in","local","nil","not","or","repeat","return",
                    "then","true","until","while" };
                if (!Used.Contains(n) && !kw.Contains(n)) { Used.Add(n); return n; }
            }
            // fallback unique
            string fb = "_v" + Used.Count;
            Used.Add(fb);
            return fb;
        }
    }

    // ── Bytecode definitions ─────────────────────────────────────────────────────
    enum Op : byte
    {
        LOADNIL=0,LOADBOOL=1,LOADK=2,MOVE=3,GETGLOBAL=4,SETGLOBAL=5,
        GETTABLE=6,SETTABLE=7,NEWTABLE=8,ADD=9,SUB=10,MUL=11,DIV=12,
        MOD=13,POW=14,CONCAT=15,UNM=16,NOT=17,LEN=18,
        EQ=19,LT=20,LE=21,JMP=22,TEST=23,CALL=24,RETURN=25,
        CLOSURE=26,VARARG=27,FORPREP=28,FORLOOP=29,GETUPVAL=30,SETUPVAL=31,
        SELF=32,BAND=33,BOR=34,BXOR=35,BNOT=36,IDIV=37,SHL=38,SHR=39,
    }

    class Ins
    {
        public Op op; public int a,b,c,bx,sbx; public bool isABC;
        public static Ins ABC(Op o,int a,int b,int c) => new Ins{op=o,a=a,b=b,c=c,isABC=true};
        public static Ins ABx(Op o,int a,int bx) => new Ins{op=o,a=a,bx=bx,isABC=false};
        public static Ins ASBx(Op o,int a,int sbx) => new Ins{op=o,a=a,sbx=sbx,isABC=false};
    }

    enum CType : byte { Nil=0,Bool=1,Int=2,Float=3,Str=4 }

    class Con
    {
        public CType t; public bool bv; public long iv; public double fv; public string sv;
        public static Con Nil() => new Con{t=CType.Nil};
        public static Con Bool(bool v) => new Con{t=CType.Bool,bv=v};
        public static Con Int(long v) => new Con{t=CType.Int,iv=v};
        public static Con Float(double v) => new Con{t=CType.Float,fv=v};
        public static Con Str(string v) => new Con{t=CType.Str,sv=v};
    }

    class Proto
    {
        public List<Ins> Code = new List<Ins>();
        public List<Con> Consts = new List<Con>();
        public List<Proto> Protos = new List<Proto>();
        public int Params=0; public bool IsVarArg=true; public int MaxStack=16;
        public List<string> Upvals = new List<string>();
    }

    // ── Compiler: turns a Lua source string into a Proto tree ────────────────────
    // (We do a simplified single-pass; real usage would plug in a full Luau parser)
    class Compiler
    {
        // Tokenizer types
        enum TK { Name,Number,Str,Op,LParen,RParen,LBrace,RBrace,LBrack,RBrack,
                  Comma,Dot,Colon,Semi,Eq,NEq,Lt,Le,Gt,Ge,Assign,DotDot,
                  And,Or,Not,Local,Function,Return,If,Then,Else,Elseif,End,
                  Do,While,Repeat,Until,For,In,Break,True,False,Nil,EOF }

        struct Token { public TK type; public string val; }

        string _src; int _pos; List<Token> _tokens = new List<Token>();
        Proto _root; Proto _cur;
        int _regTop;
        Dictionary<string,int> _locals = new Dictionary<string,int>();
        List<Con> _K => _cur.Consts;
        List<Ins> _C => _cur.Code;

        public Proto Compile(string src)
        {
            _src = src; _pos = 0;
            Tokenize();
            _root = new Proto { IsVarArg=true, MaxStack=64 };
            _cur = _root; _regTop=0;
            ParseBlock();
            if (_C.Count==0 || _C[_C.Count-1].op != Op.RETURN)
                _C.Add(Ins.ABC(Op.RETURN,0,1,0));
            return _root;
        }

        // ── Tokenizer ─────────────────────────────────────────────────────────────
        void Tokenize()
        {
            int i=0; string s=_src;
            while (i<s.Length)
            {
                char c=s[i];
                if (c==' '||c=='\t'||c=='\r'||c=='\n'){i++;continue;}
                if (c=='-'&&i+1<s.Length&&s[i+1]=='-')
                {
                    i+=2;
                    if (i<s.Length&&s[i]=='[')
                    {
                        // long comment – skip for now; just eat line
                    }
                    while (i<s.Length&&s[i]!='\n') i++;
                    continue;
                }
                if (char.IsLetter(c)||c=='_')
                {
                    int st=i; while (i<s.Length&&(char.IsLetterOrDigit(s[i])||s[i]=='_')) i++;
                    string w=s.Substring(st,i-st);
                    TK kw;
                    switch(w){
                        case "and": kw=TK.And;break; case "or": kw=TK.Or;break;
                        case "not": kw=TK.Not;break; case "local": kw=TK.Local;break;
                        case "function": kw=TK.Function;break; case "return": kw=TK.Return;break;
                        case "if": kw=TK.If;break; case "then": kw=TK.Then;break;
                        case "else": kw=TK.Else;break; case "elseif": kw=TK.Elseif;break;
                        case "end": kw=TK.End;break; case "do": kw=TK.Do;break;
                        case "while": kw=TK.While;break; case "repeat": kw=TK.Repeat;break;
                        case "until": kw=TK.Until;break; case "for": kw=TK.For;break;
                        case "in": kw=TK.In;break; case "break": kw=TK.Break;break;
                        case "true": kw=TK.True;break; case "false": kw=TK.False;break;
                        case "nil": kw=TK.Nil;break;
                        default: kw=TK.Name;break;
                    }
                    _tokens.Add(new Token{type=kw,val=w}); continue;
                }
                if (char.IsDigit(c)||(c=='.'&&i+1<s.Length&&char.IsDigit(s[i+1])))
                {
                    int st=i;
                    while (i<s.Length&&(char.IsLetterOrDigit(s[i])||s[i]=='.'||s[i]=='x'||s[i]=='X')) i++;
                    _tokens.Add(new Token{type=TK.Number,val=s.Substring(st,i-st)}); continue;
                }
                if (c=='"'||c=='\'')
                {
                    char q=c; i++;
                    var sb=new StringBuilder();
                    while (i<s.Length&&s[i]!=q)
                    {
                        if (s[i]=='\\'&&i+1<s.Length){i++;sb.Append(s[i]);}
                        else sb.Append(s[i]);
                        i++;
                    }
                    i++;
                    _tokens.Add(new Token{type=TK.Str,val=sb.ToString()}); continue;
                }
                if (c=='[')
                {
                    if (i+1<s.Length&&(s[i+1]=='['||s[i+1]=='='))
                    {
                        // long string
                        int eq=0; int j=i+1;
                        while (j<s.Length&&s[j]=='='){eq++;j++;}
                        if (j<s.Length&&s[j]=='[')
                        {
                            j++;
                            string close="]"+new string('=',eq)+"]";
                            int end=s.IndexOf(close,j);
                            if (end<0) end=s.Length;
                            _tokens.Add(new Token{type=TK.Str,val=s.Substring(j,end-j)});
                            i=end+close.Length; continue;
                        }
                    }
                    _tokens.Add(new Token{type=TK.LBrack}); i++; continue;
                }
                switch(c){
                    case '(': _tokens.Add(new Token{type=TK.LParen});i++;break;
                    case ')': _tokens.Add(new Token{type=TK.RParen});i++;break;
                    case '{': _tokens.Add(new Token{type=TK.LBrace});i++;break;
                    case '}': _tokens.Add(new Token{type=TK.RBrace});i++;break;
                    case ']': _tokens.Add(new Token{type=TK.RBrack});i++;break;
                    case ',': _tokens.Add(new Token{type=TK.Comma});i++;break;
                    case ';': _tokens.Add(new Token{type=TK.Semi});i++;break;
                    case ':': _tokens.Add(new Token{type=TK.Colon});i++;break;
                    case '#': _tokens.Add(new Token{type=TK.Op,val="#"});i++;break;
                    case '+': _tokens.Add(new Token{type=TK.Op,val="+"});i++;break;
                    case '-': _tokens.Add(new Token{type=TK.Op,val="-"});i++;break;
                    case '*': _tokens.Add(new Token{type=TK.Op,val="*"});i++;break;
                    case '/':
                        if (i+1<s.Length&&s[i+1]=='/'){_tokens.Add(new Token{type=TK.Op,val="//"});i+=2;}
                        else{_tokens.Add(new Token{type=TK.Op,val="/"});i++;}break;
                    case '%': _tokens.Add(new Token{type=TK.Op,val="%"});i++;break;
                    case '^': _tokens.Add(new Token{type=TK.Op,val="^"});i++;break;
                    case '&': _tokens.Add(new Token{type=TK.Op,val="&"});i++;break;
                    case '|': _tokens.Add(new Token{type=TK.Op,val="|"});i++;break;
                    case '~':
                        if(i+1<s.Length&&s[i+1]=='='){_tokens.Add(new Token{type=TK.NEq,val="~="});i+=2;}
                        else{_tokens.Add(new Token{type=TK.Op,val="~"});i++;}break;
                    case '<':
                        if(i+1<s.Length&&s[i+1]=='='){_tokens.Add(new Token{type=TK.Le,val="<="});i+=2;}
                        else if(i+1<s.Length&&s[i+1]=='<'){_tokens.Add(new Token{type=TK.Op,val="<<"});i+=2;}
                        else{_tokens.Add(new Token{type=TK.Lt,val="<"});i++;}break;
                    case '>':
                        if(i+1<s.Length&&s[i+1]=='='){_tokens.Add(new Token{type=TK.Ge,val=">="});i+=2;}
                        else if(i+1<s.Length&&s[i+1]=='>'){_tokens.Add(new Token{type=TK.Op,val=">>"});i+=2;}
                        else{_tokens.Add(new Token{type=TK.Gt,val=">"});i++;}break;
                    case '=':
                        if(i+1<s.Length&&s[i+1]=='='){_tokens.Add(new Token{type=TK.Eq,val="=="});i+=2;}
                        else{_tokens.Add(new Token{type=TK.Assign,val="="});i++;}break;
                    case '.':
                        if(i+1<s.Length&&s[i+1]=='.'){
                            if(i+2<s.Length&&s[i+2]=='.'){_tokens.Add(new Token{type=TK.Op,val="..."});i+=3;}
                            else{_tokens.Add(new Token{type=TK.DotDot,val=".."});i+=2;}
                        }else{_tokens.Add(new Token{type=TK.Dot,val="."});i++;}break;
                    default: i++;break;
                }
            }
            _tokens.Add(new Token{type=TK.EOF});
        }

        // ── Token helpers ─────────────────────────────────────────────────────────
        int _ti=0;
        Token Peek() => _ti<_tokens.Count?_tokens[_ti]:new Token{type=TK.EOF};
        Token Eat() => _ti<_tokens.Count?_tokens[_ti++]:new Token{type=TK.EOF};
        Token Expect(TK t){ var tk=Eat(); if(tk.type!=t) throw new Exception($"Expected {t} got {tk.type} '{tk.val}'"); return tk; }
        bool Check(TK t) => Peek().type==t;
        bool Match(TK t){ if(Check(t)){Eat();return true;}return false; }

        // ── Register allocation ────────────────────────────────────────────────────
        int AllocReg() => _regTop++;
        void FreeReg() { if(_regTop>0) _regTop--; }
        int SaveTop() => _regTop;
        void RestoreTop(int t) { _regTop=t; }

        // ── Constant pool ──────────────────────────────────────────────────────────
        int AddK(Con c)
        {
            for(int i=0;i<_K.Count;i++)
            {
                var k=_K[i];
                if(k.t!=c.t) continue;
                switch(c.t){
                    case CType.Nil: return i;
                    case CType.Bool: if(k.bv==c.bv) return i; break;
                    case CType.Int: if(k.iv==c.iv) return i; break;
                    case CType.Float: if(k.fv==c.fv) return i; break;
                    case CType.Str: if(k.sv==c.sv) return i; break;
                }
            }
            _K.Add(c); return _K.Count-1;
        }

        // ── Block / statement parser ───────────────────────────────────────────────
        void ParseBlock()
        {
            while(true)
            {
                var tk=Peek();
                if(tk.type==TK.EOF||tk.type==TK.End||tk.type==TK.Else||
                   tk.type==TK.Elseif||tk.type==TK.Until) break;
                ParseStat();
                Match(TK.Semi);
            }
        }

        void ParseStat()
        {
            var tk=Peek();
            switch(tk.type)
            {
                case TK.Local:     ParseLocal(); break;
                case TK.Function:  ParseFunctionStat(); break;
                case TK.Return:    ParseReturn(); break;
                case TK.If:        ParseIf(); break;
                case TK.While:     ParseWhile(); break;
                case TK.For:       ParseFor(); break;
                case TK.Do:        Eat(); ParseBlock(); Expect(TK.End); break;
                case TK.Break:     Eat(); _C.Add(Ins.ASBx(Op.JMP,0,0)); break; // simplified
                case TK.Repeat:    ParseRepeat(); break;
                default:           ParseExprStat(); break;
            }
        }

        void ParseLocal()
        {
            Eat(); // local
            if(Check(TK.Function))
            {
                Eat();
                var name=Expect(TK.Name).val;
                int r=AllocReg();
                _locals[name]=r;
                ParseFuncBody(r);
                return;
            }
            var names=new List<string>();
            names.Add(Expect(TK.Name).val);
            while(Match(TK.Comma)) names.Add(Expect(TK.Name).val);
            var regs=new List<int>();
            foreach(var n in names) { int r=AllocReg(); regs.Add(r); _locals[n]=r; }
            if(Match(TK.Assign))
            {
                for(int i=0;i<regs.Count;i++)
                {
                    int r=ExprToReg(regs[i]);
                    if(r!=regs[i]) _C.Add(Ins.ABC(Op.MOVE,regs[i],r,0));
                    if(i<regs.Count-1&&!Check(TK.Comma)&&!Check(TK.Semi)&&Peek().type!=TK.EOF) break;
                    if(i<regs.Count-1) Match(TK.Comma);
                }
            }
        }

        void ParseFunctionStat()
        {
            Eat(); // function
            var name=Expect(TK.Name).val;
            int r=AllocReg();
            _locals[name]=r;
            ParseFuncBody(r);
        }

        void ParseFuncBody(int dest)
        {
            var saved=_cur; var savedLocals=new Dictionary<string,int>(_locals);
            var savedRegTop=_regTop; var savedTi=_ti;
            var proto=new Proto{MaxStack=64};
            _cur=proto; _regTop=0; _locals=new Dictionary<string,int>(savedLocals);

            Expect(TK.LParen);
            var parms=new List<string>();
            if(!Check(TK.RParen))
            {
                if(Peek().type==TK.Op&&Peek().val=="..."){ Eat(); proto.IsVarArg=true; }
                else
                {
                    parms.Add(Expect(TK.Name).val);
                    while(Match(TK.Comma))
                    {
                        if(Peek().type==TK.Op&&Peek().val=="..."){Eat();proto.IsVarArg=true;break;}
                        parms.Add(Expect(TK.Name).val);
                    }
                }
            }
            Expect(TK.RParen);
            proto.Params=parms.Count;
            for(int i=0;i<parms.Count;i++){_locals[parms[i]]=i;_regTop=i+1;}

            ParseBlock();
            Expect(TK.End);
            if(proto.Code.Count==0||proto.Code[proto.Code.Count-1].op!=Op.RETURN)
                proto.Code.Add(Ins.ABC(Op.RETURN,0,1,0));

            _cur=saved;
            _regTop=savedRegTop;
            _locals=savedLocals;
            int pidx=saved.Protos.Count;
            saved.Protos.Add(proto);
            _C.Add(Ins.ABx(Op.CLOSURE,dest,pidx));
        }

        void ParseReturn()
        {
            Eat();
            var tk=Peek();
            if(tk.type==TK.EOF||tk.type==TK.End||tk.type==TK.Else||
               tk.type==TK.Elseif||tk.type==TK.Until||tk.type==TK.Semi)
            { _C.Add(Ins.ABC(Op.RETURN,0,1,0)); return; }
            int base_=SaveTop();
            var vals=new List<int>();
            vals.Add(ExprToNewReg());
            while(Match(TK.Comma)) vals.Add(ExprToNewReg());
            _C.Add(Ins.ABC(Op.RETURN,vals[0],vals.Count+1,0));
            RestoreTop(base_);
        }

        void ParseIf()
        {
            Eat(); // if
            int condR=ExprToNewReg();
            FreeReg();
            Expect(TK.Then);
            // TEST + JMP
            int testPc=_C.Count;
            _C.Add(Ins.ABC(Op.TEST,condR,0,0));
            int jmpPc=_C.Count;
            _C.Add(Ins.ASBx(Op.JMP,0,0));
            ParseBlock();
            var endJmps=new List<int>();
            while(Check(TK.Elseif)||Check(TK.Else))
            {
                int ej=_C.Count;
                _C.Add(Ins.ASBx(Op.JMP,0,0));
                endJmps.Add(ej);
                // patch previous jmp
                _C[jmpPc]=Ins.ASBx(Op.JMP,0,_C.Count-jmpPc-1);
                if(Check(TK.Elseif))
                {
                    Eat();
                    condR=ExprToNewReg(); FreeReg();
                    Expect(TK.Then);
                    testPc=_C.Count;
                    _C.Add(Ins.ABC(Op.TEST,condR,0,0));
                    jmpPc=_C.Count;
                    _C.Add(Ins.ASBx(Op.JMP,0,0));
                    ParseBlock();
                }
                else
                {
                    Eat();
                    ParseBlock();
                    _C[jmpPc]=Ins.ASBx(Op.JMP,0,_C.Count-jmpPc-1);
                    jmpPc=-1;
                    break;
                }
            }
            Expect(TK.End);
            if(jmpPc>=0) _C[jmpPc]=Ins.ASBx(Op.JMP,0,_C.Count-jmpPc-1);
            int here=_C.Count;
            foreach(var e in endJmps) _C[e]=Ins.ASBx(Op.JMP,0,here-e-1);
        }

        void ParseWhile()
        {
            Eat();
            int loopStart=_C.Count;
            int condR=ExprToNewReg(); FreeReg();
            Expect(TK.Do);
            int testPc=_C.Count;
            _C.Add(Ins.ABC(Op.TEST,condR,0,0));
            int jmpPc=_C.Count;
            _C.Add(Ins.ASBx(Op.JMP,0,0));
            ParseBlock();
            Expect(TK.End);
            _C.Add(Ins.ASBx(Op.JMP,0,loopStart-_C.Count-1));
            _C[jmpPc]=Ins.ASBx(Op.JMP,0,_C.Count-jmpPc-1);
        }

        void ParseRepeat()
        {
            Eat();
            int loopStart=_C.Count;
            ParseBlock();
            Expect(TK.Until);
            int condR=ExprToNewReg(); FreeReg();
            _C.Add(Ins.ABC(Op.TEST,condR,0,1));
            _C.Add(Ins.ASBx(Op.JMP,0,loopStart-_C.Count-1));
        }

        void ParseFor()
        {
            Eat();
            var v1=Expect(TK.Name).val;
            if(Match(TK.Assign))
            {
                // numeric for
                int base_=SaveTop();
                int rLimit=AllocReg(); int rStep=AllocReg(); int rVar=AllocReg();
                _locals[v1]=rVar;
                // init
                int rInit=ExprToNewReg(); FreeReg();
                _C.Add(Ins.ABC(Op.MOVE,rLimit-3,rInit,0)); // simplified mapping
                Expect(TK.Comma);
                int rLimE=ExprToNewReg(); FreeReg();
                _C.Add(Ins.ABC(Op.MOVE,rLimit,rLimE,0));
                long step=1;
                if(Match(TK.Comma)){ var sr=ExprToNewReg(); FreeReg(); _C.Add(Ins.ABC(Op.MOVE,rStep,sr,0)); }
                else { int ki=AddK(Con.Int(1)); _C.Add(Ins.ABx(Op.LOADK,rStep,ki)); }
                Expect(TK.Do);
                int prep=_C.Count;
                _C.Add(Ins.ASBx(Op.FORPREP,rLimit-3,0));
                int loopStart=_C.Count;
                _locals[v1]=rVar;
                ParseBlock();
                Expect(TK.End);
                _C.Add(Ins.ASBx(Op.FORLOOP,rLimit-3,loopStart-_C.Count-1));
                _C[prep]=Ins.ASBx(Op.FORPREP,rLimit-3,_C.Count-prep-2);
                RestoreTop(base_);
            }
            else
            {
                // generic for (simplified)
                Expect(TK.Comma);
                ParseBlock();
                Expect(TK.End);
            }
        }

        void ParseExprStat()
        {
            int base_=SaveTop();
            int r=ExprToNewReg();
            // check for assignment
            if(Check(TK.Assign)||Check(TK.Comma))
            {
                // multi-assign (simplified: single assign)
                if(Check(TK.Assign))
                {
                    Eat();
                    int rhs=ExprToNewReg();
                    // r is lvalue – if it's a local just move
                    if(r<_regTop-2) _C.Add(Ins.ABC(Op.MOVE,r,rhs,0));
                    FreeReg();
                }
            }
            // call result: nothing to do if already emitted CALL
            RestoreTop(base_);
        }

        // ── Expression parser ──────────────────────────────────────────────────────
        int ExprToNewReg()
        {
            int r=AllocReg();
            ExprToReg(r);
            return r;
        }

        int ExprToReg(int dest)
        {
            return ParseOr(dest);
        }

        int ParseOr(int dest)
        {
            int r=ParseAnd(dest);
            while(Check(TK.Or))
            {
                Eat();
                int r2=ExprToNewReg(); FreeReg();
                _C.Add(Ins.ABC(Op.BOR,dest,r,r2));
                r=dest;
            }
            return r;
        }

        int ParseAnd(int dest)
        {
            int r=ParseCmp(dest);
            while(Check(TK.And))
            {
                Eat();
                int r2=ExprToNewReg(); FreeReg();
                _C.Add(Ins.ABC(Op.BAND,dest,r,r2));
                r=dest;
            }
            return r;
        }

        int ParseCmp(int dest)
        {
            int r=ParseBitOr(dest);
            TK t=Peek().type;
            if(t==TK.Eq||t==TK.NEq||t==TK.Lt||t==TK.Le||t==TK.Gt||t==TK.Ge)
            {
                Eat();
                int r2=ExprToNewReg(); FreeReg();
                Op op;
                switch(t){
                    case TK.Eq:  op=Op.EQ;break;
                    case TK.NEq: op=Op.EQ;break;
                    case TK.Lt:  op=Op.LT;break;
                    case TK.Le:  op=Op.LE;break;
                    case TK.Gt:  op=Op.LT;int tmp=r;r=r2;r2=tmp;break;
                    default:     op=Op.LE;tmp=r;r=r2;r2=tmp;break;
                }
                _C.Add(Ins.ABC(op,t==TK.NEq?0:1,r,r2));
                _C.Add(Ins.ASBx(Op.JMP,0,1));
                _C.Add(Ins.ABC(Op.LOADBOOL,dest,0,1));
                _C.Add(Ins.ABC(Op.LOADBOOL,dest,1,0));
                r=dest;
            }
            return r;
        }

        int ParseBitOr(int dest)
        {
            int r=ParseBitXor(dest);
            while(Check(TK.Op)&&Peek().val=="|")
            { Eat(); int r2=ExprToNewReg(); FreeReg(); _C.Add(Ins.ABC(Op.BOR,dest,r,r2)); r=dest; }
            return r;
        }
        int ParseBitXor(int dest)
        {
            int r=ParseBitAnd(dest);
            while(Check(TK.Op)&&Peek().val=="~")
            { Eat(); int r2=ExprToNewReg(); FreeReg(); _C.Add(Ins.ABC(Op.BXOR,dest,r,r2)); r=dest; }
            return r;
        }
        int ParseBitAnd(int dest)
        {
            int r=ParseShift(dest);
            while(Check(TK.Op)&&Peek().val=="&")
            { Eat(); int r2=ExprToNewReg(); FreeReg(); _C.Add(Ins.ABC(Op.BAND,dest,r,r2)); r=dest; }
            return r;
        }
        int ParseShift(int dest)
        {
            int r=ParseConcat(dest);
            while(Check(TK.Op)&&(Peek().val=="<<"||Peek().val==">>"))
            { var op=Eat().val; int r2=ExprToNewReg(); FreeReg();
              _C.Add(Ins.ABC(op=="<<"?Op.SHL:Op.SHR,dest,r,r2)); r=dest; }
            return r;
        }
        int ParseConcat(int dest)
        {
            int r=ParseAdd(dest);
            if(Check(TK.DotDot))
            { Eat(); int r2=ExprToNewReg(); FreeReg(); _C.Add(Ins.ABC(Op.CONCAT,dest,r,r2)); r=dest; }
            return r;
        }
        int ParseAdd(int dest)
        {
            int r=ParseMul(dest);
            while(Check(TK.Op)&&(Peek().val=="+"||Peek().val=="-"))
            {
                var op=Eat().val; int r2=ExprToNewReg(); FreeReg();
                _C.Add(Ins.ABC(op=="+"?Op.ADD:Op.SUB,dest,r,r2)); r=dest;
            }
            return r;
        }
        int ParseMul(int dest)
        {
            int r=ParseUnary(dest);
            while(Check(TK.Op)&&(Peek().val=="*"||Peek().val=="/"||Peek().val=="%"||Peek().val=="//"))
            {
                var op=Eat().val; int r2=ExprToNewReg(); FreeReg();
                Op opi=op=="*"?Op.MUL:op=="/"?Op.DIV:op=="%"?Op.MOD:Op.IDIV;
                _C.Add(Ins.ABC(opi,dest,r,r2)); r=dest;
            }
            return r;
        }
        int ParseUnary(int dest)
        {
            if(Check(TK.Op)&&Peek().val=="-")
            { Eat(); int r=ParsePow(dest); _C.Add(Ins.ABC(Op.UNM,dest,r,0)); return dest; }
            if(Check(TK.Not))
            { Eat(); int r=ParsePow(dest); _C.Add(Ins.ABC(Op.NOT,dest,r,0)); return dest; }
            if(Check(TK.Op)&&Peek().val=="#")
            { Eat(); int r=ParsePow(dest); _C.Add(Ins.ABC(Op.LEN,dest,r,0)); return dest; }
            if(Check(TK.Op)&&Peek().val=="~")
            { Eat(); int r=ParsePow(dest); _C.Add(Ins.ABC(Op.BNOT,dest,r,0)); return dest; }
            return ParsePow(dest);
        }
        int ParsePow(int dest)
        {
            int r=ParsePostfix(dest);
            if(Check(TK.Op)&&Peek().val=="^")
            { Eat(); int r2=ExprToNewReg(); FreeReg(); _C.Add(Ins.ABC(Op.POW,dest,r,r2)); r=dest; }
            return r;
        }

        int ParsePostfix(int dest)
        {
            int r=ParsePrimary(dest);
            while(true)
            {
                if(Check(TK.Dot))
                {
                    Eat();
                    var field=Expect(TK.Name).val;
                    int ki=AddK(Con.Str(field));
                    _C.Add(Ins.ABC(Op.GETTABLE,dest,r,256+ki));
                    r=dest;
                }
                else if(Check(TK.LBrack))
                {
                    Eat();
                    int idx=ExprToNewReg(); FreeReg();
                    Expect(TK.RBrack);
                    _C.Add(Ins.ABC(Op.GETTABLE,dest,r,idx));
                    r=dest;
                }
                else if(Check(TK.LParen)||Check(TK.LBrace)||Check(TK.Str))
                {
                    r=ParseCall(dest,r,-1);
                }
                else if(Check(TK.Colon))
                {
                    Eat();
                    var method=Expect(TK.Name).val;
                    int ki=AddK(Con.Str(method));
                    _C.Add(Ins.ABC(Op.SELF,dest,r,256+ki));
                    r=ParseCall(dest,dest,-1);
                }
                else break;
            }
            return r;
        }

        int ParseCall(int dest, int funcReg, int wantRet)
        {
            int base_=funcReg+1;
            int savedTop=_regTop;
            _regTop=base_;
            var args=new List<int>();
            if(Check(TK.LParen))
            {
                Eat();
                if(!Check(TK.RParen))
                {
                    args.Add(ExprToNewReg());
                    while(Match(TK.Comma)) args.Add(ExprToNewReg());
                }
                Expect(TK.RParen);
            }
            else if(Check(TK.LBrace))
            {
                args.Add(ParseTable(AllocReg()));
            }
            else if(Check(TK.Str))
            {
                var sv=Eat().val;
                int ki=AddK(Con.Str(sv));
                int r=AllocReg();
                _C.Add(Ins.ABx(Op.LOADK,r,ki));
                args.Add(r);
            }
            int narg=args.Count+1;
            int nret=wantRet<0?1:wantRet+1;
            _C.Add(Ins.ABC(Op.CALL,funcReg,narg,nret));
            _regTop=savedTop;
            if(wantRet!=0) { _C.Add(Ins.ABC(Op.MOVE,dest,funcReg,0)); }
            return dest;
        }

        int ParsePrimary(int dest)
        {
            var tk=Peek();
            switch(tk.type)
            {
                case TK.Number:
                {
                    Eat();
                    double d; long l;
                    if(long.TryParse(tk.val,out l))
                    { int ki=AddK(Con.Int(l)); _C.Add(Ins.ABx(Op.LOADK,dest,ki)); }
                    else if(tk.val.StartsWith("0x")||tk.val.StartsWith("0X"))
                    { l=Convert.ToInt64(tk.val,16); int ki=AddK(Con.Int(l)); _C.Add(Ins.ABx(Op.LOADK,dest,ki)); }
                    else if(double.TryParse(tk.val,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out d))
                    { int ki=AddK(Con.Float(d)); _C.Add(Ins.ABx(Op.LOADK,dest,ki)); }
                    return dest;
                }
                case TK.Str:
                {
                    Eat();
                    int ki=AddK(Con.Str(tk.val));
                    _C.Add(Ins.ABx(Op.LOADK,dest,ki));
                    return dest;
                }
                case TK.True:
                    Eat(); _C.Add(Ins.ABC(Op.LOADBOOL,dest,1,0)); return dest;
                case TK.False:
                    Eat(); _C.Add(Ins.ABC(Op.LOADBOOL,dest,0,0)); return dest;
                case TK.Nil:
                    Eat(); _C.Add(Ins.ABC(Op.LOADNIL,dest,0,0)); return dest;
                case TK.Name:
                {
                    Eat();
                    if(_locals.TryGetValue(tk.val,out int lr)) return lr;
                    int ki=AddK(Con.Str(tk.val));
                    _C.Add(Ins.ABx(Op.GETGLOBAL,dest,ki));
                    return dest;
                }
                case TK.LParen:
                {
                    Eat();
                    int r=ExprToReg(dest);
                    Expect(TK.RParen);
                    return r;
                }
                case TK.Function:
                {
                    Eat();
                    ParseFuncBody(dest);
                    return dest;
                }
                case TK.LBrace:
                    return ParseTable(dest);
                case TK.Op when tk.val=="...":
                    Eat(); _C.Add(Ins.ABC(Op.VARARG,dest,1,0)); return dest;
                default:
                    return dest;
            }
        }

        int ParseTable(int dest)
        {
            Expect(TK.LBrace);
            _C.Add(Ins.ABC(Op.NEWTABLE,dest,0,0));
            int idx=1;
            while(!Check(TK.RBrace)&&Peek().type!=TK.EOF)
            {
                if(Check(TK.LBrack))
                {
                    Eat();
                    int kr=ExprToNewReg(); FreeReg();
                    Expect(TK.RBrack);
                    Expect(TK.Assign);
                    int vr=ExprToNewReg(); FreeReg();
                    _C.Add(Ins.ABC(Op.SETTABLE,dest,kr,vr));
                }
                else if(Peek().type==TK.Name&&_ti+1<_tokens.Count&&_tokens[_ti+1].type==TK.Assign)
                {
                    var fn=Eat().val; Eat();
                    int ki=AddK(Con.Str(fn));
                    int vr=ExprToNewReg(); FreeReg();
                    _C.Add(Ins.ABC(Op.SETTABLE,dest,256+ki,vr));
                }
                else
                {
                    int vr=ExprToNewReg(); FreeReg();
                    int ki=AddK(Con.Int(idx++));
                    _C.Add(Ins.ABC(Op.SETTABLE,dest,256+ki,vr));
                }
                if(!Match(TK.Comma)) Match(TK.Semi);
            }
            Expect(TK.RBrace);
            return dest;
        }
    }

    // ── Bytecode serializer ──────────────────────────────────────────────────────
    class Serializer
    {
        List<byte> _b=new List<byte>();
        void U8(byte v)=>_b.Add(v);
        void U16(ushort v){_b.Add((byte)(v&0xFF));_b.Add((byte)(v>>8));}
        void U32(uint v){_b.Add((byte)(v&0xFF));_b.Add((byte)((v>>8)&0xFF));_b.Add((byte)((v>>16)&0xFF));_b.Add((byte)((v>>24)&0xFF));}
        void I64(long v)=>U64((ulong)v);
        void U64(ulong v){for(int i=0;i<8;i++){_b.Add((byte)(v&0xFF));v>>=8;}}
        void F64(double v){_b.AddRange(BitConverter.GetBytes(v));}
        void Str(string s){if(s==null){U32(0);return;}var bytes=Encoding.UTF8.GetBytes(s);U32((uint)bytes.Length);_b.AddRange(bytes);}

        public byte[] Serialize(Proto p)
        {
            _b.Clear();
            _b.AddRange(new byte[]{0xDE,0xAD,0xBE,0xEF,0x01});
            SProto(p);
            return _b.ToArray();
        }

        void SProto(Proto p)
        {
            U8((byte)p.Params);
            U8(p.IsVarArg?(byte)1:(byte)0);
            U8((byte)p.MaxStack);
            U32((uint)p.Code.Count);
            foreach(var ins in p.Code)
            {
                U8((byte)ins.op);
                U8((byte)(ins.a&0xFF));
                if(ins.isABC)
                {
                    U8((byte)(ins.b&0xFF));
                    U8((byte)(ins.c&0xFF));
                }
                else
                {
                    U16((ushort)(ins.bx&0xFFFF));
                    U16((ushort)((ins.sbx+32767)&0xFFFF));
                }
            }
            U32((uint)p.Consts.Count);
            foreach(var c in p.Consts)
            {
                U8((byte)c.t);
                switch(c.t){
                    case CType.Bool:  U8(c.bv?(byte)1:(byte)0);break;
                    case CType.Int:   I64(c.iv);break;
                    case CType.Float: F64(c.fv);break;
                    case CType.Str:   Str(c.sv);break;
                }
            }
            U32((uint)p.Upvals.Count);
            foreach(var uv in p.Upvals) Str(uv);
            U32((uint)p.Protos.Count);
            foreach(var ch in p.Protos) SProto(ch);
        }
    }

    // ── Z85 encoder ─────────────────────────────────────────────────────────────
    static class Z85
    {
        const string C="0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.-:+=^!/*?&<>()[]{}@%$#";
        public static string Encode(byte[] data)
        {
            int pad=(4-(data.Length%4))%4;
            if(pad>0){var d2=new byte[data.Length+pad];Array.Copy(data,d2,data.Length);data=d2;}
            var sb=new StringBuilder();
            uint lv=BitConverter.ToUInt32(BitConverter.GetBytes((uint)(data.Length-pad)),0);
            var lc=new char[5];for(int i=4;i>=0;i--){lc[i]=C[(int)(lv%85)];lv/=85;}sb.Append(lc);
            for(int i=0;i<data.Length;i+=4)
            {
                uint v=((uint)data[i]<<24)|((uint)data[i+1]<<16)|((uint)data[i+2]<<8)|(uint)data[i+3];
                var ch=new char[5];for(int j=4;j>=0;j--){ch[j]=C[(int)(v%85)];v/=85;}sb.Append(ch);
            }
            return sb.ToString();
        }
    }

    // ── CRC32 ────────────────────────────────────────────────────────────────────
    static class CRC
    {
        public static uint Calc(byte[] data)
        {
            uint c=0xDEADBEEF;
            foreach(byte b in data){c^=b;for(int i=0;i<8;i++)c=(c&1)==1?(c>>1)^0xEDB88320u:c>>1;}
            return c;
        }
    }

    // ── Single-line VM + loader generator ────────────────────────────────────────
    class VMGen
    {
        // All VM code is generated as a single line
        public string Generate(string z85, uint crc)
        {
            Names.Reset();
            // Allocate all names up front
            var N=new Func<string>(()=>Names.Gen());
            string
              nRaw=N(),nSent=N(),nClean=N(),nChars=N(),nMap=N(),nDec=N(),
              nI=N(),nJ=N(),nCh=N(),nAcc=N(),nOut=N(),nLen=N(),nBytes=N(),
              nPos=N(),nRu8=N(),nRu16=N(),nRu32=N(),nRi64=N(),nRf64=N(),nRstr=N(),
              nRpro=N(),nV=N(),nB1=N(),nB2=N(),nM=N(),nCnt=N(),nCt=N(),nMag=N(),
              nRoot=N(),nExec=N(),nEnv=N(),nStk=N(),nIp=N(),nInsn=N(),nOp=N(),
              nA=N(),nB=N(),nC=N(),nBx=N(),nSbx=N(),nK=N(),nP=N(),nUpv=N(),
              nArgs=N(),nCall=N(),nFn=N(),nRet=N(),nSz=N(),nNa=N(),nNr=N(),
              nProto=N(),nPidx=N(),nUv2=N(),nCF=N(),nHi=N(),nLo=N(),nSg=N(),
              nEx=N(),nMn=N(),nParts=N(),nRes=N(),nIargs=N(),
              // junk names
              j1=N(),j2=N(),j3=N(),j4=N(),j5=N(),j6=N(),j7=N(),j8=N();

            int jv1=Rng.Next(0x1000,0xFFFE),jv2=Rng.Next(0x1000,0xFFFE),
                jv3=Rng.Next(1,99),jv4=Rng.Next(1,99),jv5=Rng.Next(1000,9999);

            string sentinel=string.Concat(Enumerable.Repeat("+<VdL>",108));
            int mid=z85.Length/2;
            string embedded=z85.Substring(0,mid)+sentinel+z85.Substring(mid);

            // Build as one single line
            var sb=new StringBuilder();

            // Header comment (Luraph-style)
            sb.Append($"-- This file was protected using CustomVM Obfuscator v2.3 [https://example.com/]\n");
            sb.Append($"return (function(...)");

            // ── junk vars (single line style, semicolons) ───────────────────────
            sb.Append($"local {j1},{j2}=0x{jv1:X4},0x{jv2:X4};");
            sb.Append($"local {j3}=bit32.bxor({j1},{j2});");
            sb.Append($"local {j4}=0x{jv3:X};");
            sb.Append($"local {j5}=bit32.band({j1},{j2});");
            sb.Append($"local {j6}=({j4}*{jv4})-{jv4}*{j4};");
            sb.Append($"local {j7}=bit32.bor({j3},{j5});");
            sb.Append($"local {j8}=0;while false do {j8}={j8}+1 end;");

            // ── Z85 encoded bytecode with sentinel ─────────────────────────────
            sb.Append($"local {nRaw}=[[{embedded}]];");

            // ── Strip sentinel ──────────────────────────────────────────────────
            sb.Append($"local {nSent}=\"+<VdL>\";");
            sb.Append($"local {nClean}=(string.gsub({nRaw},{nSent},\"\"));");

            // ── Z85 alphabet + map ──────────────────────────────────────────────
            sb.Append($"local {nChars}=\"0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.-:+=^!/*?&<>()[]{{}}@%$#\";");
            sb.Append($"local {nMap}={{}};");
            sb.Append($"for {nI}=1,#{nChars} do {nMap}[string.sub({nChars},{nI},{nI})]={nI}-1;end;");

            // ── Z85 decode function ─────────────────────────────────────────────
            sb.Append($"local function {nDec}({nRaw})");
            sb.Append($"local {nOut}={{}};local {nI}=1;local {nLen}=0;");
            sb.Append($"for _=1,5 do local {nCh}=string.sub({nRaw},{nI},{nI});{nLen}={nLen}*85+({nMap}[{nCh}] or 0);{nI}={nI}+1;end;");
            sb.Append($"local {nJ}=0;");
            sb.Append($"while {nI}<=#({nRaw}) do local {nAcc}=0;");
            sb.Append($"for _=1,5 do local {nCh}=string.sub({nRaw},{nI},{nI});{nAcc}={nAcc}*85+({nMap}[{nCh}] or 0);{nI}={nI}+1;end;");
            sb.Append($"{nOut}[{nJ}+1]=bit32.band(bit32.rshift({nAcc},24),0xFF);");
            sb.Append($"{nOut}[{nJ}+2]=bit32.band(bit32.rshift({nAcc},16),0xFF);");
            sb.Append($"{nOut}[{nJ}+3]=bit32.band(bit32.rshift({nAcc},8),0xFF);");
            sb.Append($"{nOut}[{nJ}+4]=bit32.band({nAcc},0xFF);");
            sb.Append($"{nJ}={nJ}+4;end;");
            sb.Append($"while #{nOut}>{nLen} do {nOut}[#{nOut}]=nil;end;");
            sb.Append($"return {nOut};end;");

            // ── Decode ──────────────────────────────────────────────────────────
            sb.Append($"local {nBytes}={nDec}({nClean});");

            // ── Anti-tamper (CRC32 on decoded bytes) ────────────────────────────
            string nCrc=Names.Gen(),nCi=Names.Gen(),nCb=Names.Gen(),nCl=Names.Gen();
            sb.Append($"local {nCrc}=0xDEADBEEF;local {nCl}=#{nBytes};");
            sb.Append($"for {nCi}=1,{nCl} do local {nCb}={nBytes}[{nCi}] or 0;{nCrc}=bit32.bxor({nCrc},{nCb});");
            sb.Append($"for _=1,8 do if bit32.band({nCrc},1)==1 then {nCrc}=bit32.bxor(bit32.rshift({nCrc},1),0xEDB88320);");
            sb.Append($"else {nCrc}=bit32.rshift({nCrc},1);end;end;end;");
            sb.Append($"{nCrc}=bit32.band({nCrc},0xFFFFFFFF);");
            sb.Append($"if {nCrc}~=0x{crc:X8} then error(\"Integrity violation [{jv5}]\");end;");

            // ── Deserializer ────────────────────────────────────────────────────
            sb.Append($"local {nPos}=1;");
            sb.Append($"local function {nRu8}() local {nV}={nBytes}[{nPos}] or 0;{nPos}={nPos}+1;return {nV};end;");
            sb.Append($"local function {nRu16}() local {nB1}={nRu8}();local {nB2}={nRu8}();return {nB1}+{nB2}*256;end;");
            sb.Append($"local function {nRu32}() local {nB1}={nRu16}();local {nB2}={nRu16}();return {nB1}+{nB2}*65536;end;");
            sb.Append($"local function {nRi64}() local {nV}=0;local {nM}=1;for _=1,8 do {nV}={nV}+{nRu8}()*{nM};{nM}={nM}*256;end;return {nV};end;");
            sb.Append($"local function {nRf64}() local b={{}};for _=1,8 do b[_]={nRu8}();end;");
            sb.Append($"local {nHi},{ nLo}=0,0;for k=8,5,-1 do {nHi}={nHi}*256+b[k];end;for k=4,1,-1 do {nLo}={nLo}*256+b[k];end;");
            sb.Append($"local {nSg}=math.floor({nHi}/0x80000000);");
            sb.Append($"local {nEx}=bit32.band(math.floor({nHi}/0x100000),0x7FF);");
            sb.Append($"local {nMn}=({nHi}%0x100000)*0x100000000+{nLo};");
            sb.Append($"if {nEx}==0x7FF then return {nSg}==0 and math.huge or -math.huge;");
            sb.Append($"elseif {nEx}==0 then return math.ldexp({nMn}/0x10000000000000,-1022)*(1-2*{nSg});");
            sb.Append($"else return math.ldexp(({nMn}/0x10000000000000)+1,{nEx}-1023)*({nSg}==0 and 1 or -1);end;end;");
            sb.Append($"local function {nRstr}() local {nCnt}={nRu32}();if {nCnt}==0 then return nil;end;");
            sb.Append($"local {nV}={{}};for _=1,{nCnt} do {nV}[_]=string.char({nRu8}());end;return table.concat({nV});end;");

            // proto deserializer
            sb.Append($"local function {nRpro}() local {nV}={{}};");
            sb.Append($"{nV}.params={nRu8}();{nV}.isva={nRu8}()==1;{nV}.ms={nRu8}();");
            sb.Append($"local {nCnt}={nRu32}();{nV}.code={{}};");
            sb.Append($"for _=1,{nCnt} do local o={nRu8}(),a={nRu8}(),b={nRu8}(),c={nRu8}();");
            sb.Append($"local bx=b+c*256;local sbx=bx-32767;");
            sb.Append($"{nV}.code[_]={{op=o,a=a,b=b,c=c,bx=bx,sbx=sbx}};end;");
            sb.Append($"local {nCnt}={nRu32}();{nV}.consts={{}};");
            sb.Append($"for _=1,{nCnt} do local ct={nRu8}();");
            sb.Append($"if ct==0 then {nV}.consts[_]={{t=0,v=nil}};");
            sb.Append($"elseif ct==1 then {nV}.consts[_]={{t=1,v={nRu8}()==1}};");
            sb.Append($"elseif ct==2 then {nV}.consts[_]={{t=2,v={nRi64}()}};");
            sb.Append($"elseif ct==3 then {nV}.consts[_]={{t=3,v={nRf64}()}};");
            sb.Append($"elseif ct==4 then {nV}.consts[_]={{t=4,v={nRstr}()}};end;end;");
            sb.Append($"local {nCnt}={nRu32}();{nV}.uv={{}};for _=1,{nCnt} do {nV}.uv[_]={nRstr}();end;");
            sb.Append($"local {nCnt}={nRu32}();{nV}.pr={{}};for _=1,{nCnt} do {nV}.pr[_]={nRpro}();end;");
            sb.Append($"return {nV};end;");

            // magic check
            sb.Append($"local {nMag}={{0xDE,0xAD,0xBE,0xEF,0x01}};");
            sb.Append($"for _=1,5 do if {nBytes}[_]~={nMag}[_] then error(\"Bad magic [{jv3}]\");end;end;");
            sb.Append($"{nPos}=6;local {nRoot}={nRpro}();");

            // more junk
            sb.Append($"local {Names.Gen()}=bit32.bxor(0x{Rng.Next(0x1000,0xFFFE):X4},0x{Rng.Next(0x1000,0xFFFE):X4});");
            sb.Append($"local {Names.Gen()}=0x{Rng.Next(0x100,0xFFF):X3};");

            // ── VM executor ─────────────────────────────────────────────────────
            sb.Append($"local {nEnv}=(getfenv and getfenv(0)) or _ENV or {{}};");
            sb.Append($"local {nExec};");
            sb.Append($"{nExec}=function({nProto},{nUpv},{nArgs})");
            sb.Append($"local {nK}={nProto}.consts;local {nP}={nProto}.pr;");
            sb.Append($"local {nStk}={{}};local {nIp}=1;");
            sb.Append($"if {nArgs} then for {nI}=1,#{nArgs} do {nStk}[{nI}-1]={nArgs}[{nI}];end;end;");

            // call helper
            sb.Append($"local function {nCall}({nFn},base,{nNa},{nNr})");
            sb.Append($"if type({nFn})==\"function\" then local a={{}};");
            sb.Append($"for {nI}=0,{nNa}-1 do a[{nI}+1]={nStk}[base+{nI}];end;");
            sb.Append($"local r={{({nFn})(table.unpack(a))}};");
            sb.Append($"if {nNr}>0 then for {nI}=0,{nNr}-2 do {nStk}[base+{nI}]=r[{nI}+1];end;end;return r;");
            sb.Append($"elseif type({nFn})==\"table\" and {nFn}.__vp then");
            sb.Append($"local ta={{}};for {nI}=0,{nNa}-1 do ta[{nI}+1]={nStk}[base+{nI}];end;");
            sb.Append($"local sr={nExec}({nFn}.__vp,{nFn}.__uv,ta);");
            sb.Append($"if {nNr}>0 then for {nI}=0,{nNr}-2 do {nStk}[base+{nI}]=sr[{nI}+1];end;end;return sr;");
            sb.Append($"else error(\"call \"..type({nFn}));end;end;");

            // main dispatch loop
            sb.Append($"while true do local {nInsn}={nProto}.code[{nIp}];");
            sb.Append($"if not {nInsn} then break;end;");
            sb.Append($"local {nOp}={nInsn}.op,{nA}={nInsn}.a,{nB}={nInsn}.b,{nC}={nInsn}.c,{nBx}={nInsn}.bx,{nSbx}={nInsn}.sbx;");
            sb.Append($"{nIp}={nIp}+1;");

            // -- Fix: declare locals properly (Lua doesn't allow multiple assignment in that form)
            // Rewrite the dispatch to use proper local declarations
            // (The above is invalid Lua - fix it:)
            // Actually we need to rebuild this properly. Let me fix:

            // Rebuild the loop part cleanly:
            sb.Clear();

            // ═══════════════════════════════════════════════════════════════════
            // CLEAN REBUILD - proper single-line Lua
            // ═══════════════════════════════════════════════════════════════════
            Names.Reset();
            // re-allocate all names
            nRaw=Names.Gen();nSent=Names.Gen();nClean=Names.Gen();nChars=Names.Gen();
            nMap=Names.Gen();nDec=Names.Gen();nI=Names.Gen();nJ=Names.Gen();
            nCh=Names.Gen();nAcc=Names.Gen();nOut=Names.Gen();nLen=Names.Gen();
            nBytes=Names.Gen();nPos=Names.Gen();nRu8=Names.Gen();nRu16=Names.Gen();
            nRu32=Names.Gen();nRi64=Names.Gen();nRf64=Names.Gen();nRstr=Names.Gen();
            nRpro=Names.Gen();nV=Names.Gen();nB1=Names.Gen();nB2=Names.Gen();
            nM=Names.Gen();nCnt=Names.Gen();nCt=Names.Gen();nMag=Names.Gen();
            nRoot=Names.Gen();nExec=Names.Gen();nEnv=Names.Gen();nStk=Names.Gen();
            nIp=Names.Gen();nInsn=Names.Gen();nOp=Names.Gen();
            nA=Names.Gen();nB=Names.Gen();nC=Names.Gen();nBx=Names.Gen();nSbx=Names.Gen();
            nK=Names.Gen();nP=Names.Gen();nUpv=Names.Gen();
            nArgs=Names.Gen();nCall=Names.Gen();nFn=Names.Gen();nRet=Names.Gen();
            nSz=Names.Gen();nNa=Names.Gen();nNr=Names.Gen();
            nProto=Names.Gen();nPidx=Names.Gen();nUv2=Names.Gen();nCF=Names.Gen();
            nHi=Names.Gen();nLo=Names.Gen();nSg=Names.Gen();
            nEx=Names.Gen();nMn=Names.Gen();nParts=Names.Gen();nRes=Names.Gen();nIargs=Names.Gen();
            j1=Names.Gen();j2=Names.Gen();j3=Names.Gen();j4=Names.Gen();
            j5=Names.Gen();j6=Names.Gen();j7=Names.Gen();j8=Names.Gen();
            string nCrc2=Names.Gen(),nCi2=Names.Gen(),nCb2=Names.Gen(),nCl2=Names.Gen();
            string nTa=Names.Gen(),nSr=Names.Gen(),nSelf=Names.Gen();
            string nA2=Names.Gen(),nB2n=Names.Gen(),nT=Names.Gen(),nIdx=Names.Gen();
            string nBase=Names.Gen(),nR=Names.Gen(),nRR=Names.Gen();

            jv1=Rng.Next(0x1000,0xFFFE);jv2=Rng.Next(0x1000,0xFFFE);
            jv3=Rng.Next(1,99);jv4=Rng.Next(1,99);jv5=Rng.Next(1000,9999);

            var L=new StringBuilder();

            L.Append("-- This file was protected using CustomVM Obfuscator v2.3 [https://example.com/]\n");
            L.Append("return (function(...)");

            // junk
            L.Append($"local {j1}=0x{jv1:X4};");
            L.Append($"local {j2}=0x{jv2:X4};");
            L.Append($"local {j3}=bit32.bxor({j1},{j2});");
            L.Append($"local {j4}=bit32.band({j1},{j2});");
            L.Append($"local {j5}=bit32.bor({j3},{j4});");
            L.Append($"local {j6}=0;");
            L.Append($"local {j7}=({j1}+{j2})-{j2};");
            L.Append($"local {j8}=({j7}=={j1});");

            // raw data
            L.Append($"local {nRaw}=[[{embedded}]];");
            L.Append($"local {nSent}=\"+<VdL>\";");
            L.Append($"local {nClean}=(string.gsub({nRaw},{nSent},\"\"));");

            // z85 map
            L.Append($"local {nChars}=\"0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.-:+=^!/*?&<>()[]{{}}@%$#\";");
            L.Append($"local {nMap}={{}};");
            L.Append($"do local {nI}=1;while {nI}<=#({nChars}) do {nMap}[string.sub({nChars},{nI},{nI})]={nI}-1;{nI}={nI}+1;end;end;");

            // z85 decode
            L.Append($"local function {nDec}({nRaw2}) local {nOut}={{}};local {nI}=1;local {nLen}=0;do local {nI2}=1;while {nI2}<=5 do local {nCh}=string.sub({nRaw2},{nI},{nI});{nLen}={nLen}*85+({nMap}[{nCh}] or 0);{nI}={nI}+1;{nI2}={nI2}+1;end;end;local {nJ}=0;while {nI}<=#({nRaw2}) do local {nAcc}=0;do local {nI2}=1;while {nI2}<=5 do local {nCh}=string.sub({nRaw2},{nI},{nI});{nAcc}={nAcc}*85+({nMap}[{nCh}] or 0);{nI}={nI}+1;{nI2}={nI2}+1;end;end;{nOut}[{nJ}+1]=bit32.band(bit32.rshift({nAcc},24),0xFF);{nOut}[{nJ}+2]=bit32.band(bit32.rshift({nAcc},16),0xFF);{nOut}[{nJ}+3]=bit32.band(bit32.rshift({nAcc},8),0xFF);{nOut}[{nJ}+4]=bit32.band({nAcc},0xFF);{nJ}={nJ}+4;end;while #{nOut}>{nLen} do {nOut}[#{nOut}]=nil;end;return {nOut};end;");

            // Oops - we used nRaw2 and nI2 without allocating them. Let me fix with proper name allocation:
            string nRaw2=Names.Gen(), nI2=Names.Gen();
            // We'll rebuild the decode function with proper names now in the final version below.
            // For clarity let me produce the complete correct version:

            return BuildFinal(z85, crc, embedded, sentinel);
        }

        string BuildFinal(string z85orig, uint crc, string embedded, string sentinel)
        {
            Names.Reset();

            // ── allocate names ──────────────────────────────────────────────────
            string
              nRaw   =Names.Gen(), nRaw2  =Names.Gen(), nSent  =Names.Gen(),
              nClean =Names.Gen(), nChars =Names.Gen(), nMap   =Names.Gen(),
              nDec   =Names.Gen(), nBytes =Names.Gen(), nPos   =Names.Gen(),
              nRu8   =Names.Gen(), nRu16  =Names.Gen(), nRu32  =Names.Gen(),
              nRi64  =Names.Gen(), nRf64  =Names.Gen(), nRstr  =Names.Gen(),
              nRpro  =Names.Gen(), nRoot  =Names.Gen(), nExec  =Names.Gen(),
              nEnv   =Names.Gen(), nProto =Names.Gen(), nUpv   =Names.Gen(),
              nArgs  =Names.Gen(), nCall  =Names.Gen(), nStk   =Names.Gen(),
              nIp    =Names.Gen(), nK     =Names.Gen(), nP     =Names.Gen(),
              nInsn  =Names.Gen(), nOp    =Names.Gen(), nA     =Names.Gen(),
              nB     =Names.Gen(), nC     =Names.Gen(), nBx    =Names.Gen(),
              nSbx   =Names.Gen(), nFn    =Names.Gen(), nNa    =Names.Gen(),
              nNr    =Names.Gen(), nRet   =Names.Gen(), nIargs =Names.Gen(),
              nRes   =Names.Gen(), nParts =Names.Gen(), nSz    =Names.Gen(),
              nTa    =Names.Gen(), nSr    =Names.Gen(), nUv2   =Names.Gen(),
              nA2    =Names.Gen(), nB2n   =Names.Gen(), nT     =Names.Gen(),
              nHi    =Names.Gen(), nLo    =Names.Gen(), nSg    =Names.Gen(),
              nEx2   =Names.Gen(), nMn    =Names.Gen(), nMag   =Names.Gen(),
              // iteration
              nI     =Names.Gen(), nI2    =Names.Gen(), nI3    =Names.Gen(),
              nJ     =Names.Gen(), nJ2    =Names.Gen(),
              nCh    =Names.Gen(), nAcc   =Names.Gen(), nOut   =Names.Gen(),
              nLen   =Names.Gen(), nV     =Names.Gen(), nB1    =Names.Gen(),
              nB2    =Names.Gen(), nM     =Names.Gen(), nCnt   =Names.Gen(),
              nCt    =Names.Gen(),
              // anti-tamper
              nCrc   =Names.Gen(), nCi    =Names.Gen(), nCb2   =Names.Gen(),
              nCl    =Names.Gen(),
              // junk
              j1=Names.Gen(),j2=Names.Gen(),j3=Names.Gen(),j4=Names.Gen(),
              j5=Names.Gen(),j6=Names.Gen(),j7=Names.Gen(),j8=Names.Gen(),
              j9=Names.Gen(),j10=Names.Gen(),j11=Names.Gen(),j12=Names.Gen();

            int jv1=Rng.Next(0x1000,0xFFFE), jv2=Rng.Next(0x1000,0xFFFE),
                jv3=Rng.Next(10,99),          jv4=Rng.Next(1000,9999);

            uint crc32=crc;
            var sb=new StringBuilder();

            // ── header ──────────────────────────────────────────────────────────
            sb.Append("-- This file was protected using CustomVM Obfuscator v2.3 [https://example.com/]\n");
            sb.Append("return (function(...)");

            // ── junk ────────────────────────────────────────────────────────────
            A($"local {j1}=0x{jv1:X4}");
            A($"local {j2}=0x{jv2:X4}");
            A($"local {j3}=bit32.bxor({j1},{j2})");
            A($"local {j4}=bit32.band({j1},{j2})");
            A($"local {j5}=bit32.bor({j3},{j4})");
            A($"local {j6}=0");
            A($"local {j7}=({j1}+{j2})-{j2}");
            A($"local {j8}=({j7}=={j1})");
            A($"local {j9}=bit32.bnot({j3})");
            A($"local {j10}=({j8} and {j1} or {j2})");
            A($"local {j11}=0");
            A($"while false do {j11}={j11}+1 end");
            A($"local {j12}=bit32.rshift({j1},4)");

            // ── raw data ────────────────────────────────────────────────────────
            A($"local {nRaw}=[[{embedded}]]");
            A($"local {nSent}=\"+<VdL>\"");
            A($"local {nClean}=(string.gsub({nRaw},{nSent},\"\"))");

            // ── Z85 map ─────────────────────────────────────────────────────────
            A($"local {nChars}=\"0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.-:+=^!/*?&<>()[]{{}}@%$#\"");
            A($"local {nMap}={{}}");
            A($"do local {nI}=1;while {nI}<=#({nChars}) do {nMap}[string.sub({nChars},{nI},{nI})]={nI}-1;{nI}={nI}+1 end end");

            // ── Z85 decode function ─────────────────────────────────────────────
            sb.Append($"local function {nDec}({nRaw2})");
            sb.Append($"local {nOut}={{}};");
            sb.Append($"local {nI}=1;");
            sb.Append($"local {nLen}=0;");
            sb.Append($"do local {nI2}=1;while {nI2}<=5 do local {nCh}=string.sub({nRaw2},{nI},{nI});{nLen}={nLen}*85+({nMap}[{nCh}] or 0);{nI}={nI}+1;{nI2}={nI2}+1 end end;");
            sb.Append($"local {nJ}=0;");
            sb.Append($"while {nI}<=#({nRaw2}) do ");
            sb.Append($"local {nAcc}=0;");
            sb.Append($"do local {nI3}=1;while {nI3}<=5 do local {nCh}=string.sub({nRaw2},{nI},{nI});{nAcc}={nAcc}*85+({nMap}[{nCh}] or 0);{nI}={nI}+1;{nI3}={nI3}+1 end end;");
            sb.Append($"{nOut}[{nJ}+1]=bit32.band(bit32.rshift({nAcc},24),0xFF);");
            sb.Append($"{nOut}[{nJ}+2]=bit32.band(bit32.rshift({nAcc},16),0xFF);");
            sb.Append($"{nOut}[{nJ}+3]=bit32.band(bit32.rshift({nAcc},8),0xFF);");
            sb.Append($"{nOut}[{nJ}+4]=bit32.band({nAcc},0xFF);");
            sb.Append($"{nJ}={nJ}+4 end;");
            sb.Append($"while #{nOut}>{nLen} do {nOut}[#{nOut}]=nil end;");
            sb.Append($"return {nOut} end;");

            // ── decode ──────────────────────────────────────────────────────────
            A($"local {nBytes}={nDec}({nClean})");

            // ── anti-tamper ─────────────────────────────────────────────────────
            sb.Append($"local {nCrc}=0xDEADBEEF;");
            A($"local {nCl}=#{nBytes}");
            sb.Append($"do local {nCi}=1;while {nCi}<={nCl} do local {nCb2}={nBytes}[{nCi}] or 0;{nCrc}=bit32.bxor({nCrc},{nCb2});do local _k=1;while _k<=8 do if bit32.band({nCrc},1)==1 then {nCrc}=bit32.bxor(bit32.rshift({nCrc},1),0xEDB88320) else {nCrc}=bit32.rshift({nCrc},1) end;_k=_k+1 end end;{nCi}={nCi}+1 end end;");
            A($"{nCrc}=bit32.band({nCrc},0xFFFFFFFF)");
            sb.Append($"if {nCrc}~=0x{crc32:X8} then error(\"Integrity check failed [{jv4}]\") end;");

            // ── deserializer helpers ─────────────────────────────────────────────
            A($"local {nPos}=1");
            sb.Append($"local function {nRu8}() local {nV}={nBytes}[{nPos}] or 0;{nPos}={nPos}+1;return {nV} end;");
            sb.Append($"local function {nRu16}() local {nB1}={nRu8}();local {nB2}={nRu8}();return {nB1}+{nB2}*256 end;");
            sb.Append($"local function {nRu32}() local {nB1}={nRu16}();local {nB2}={nRu16}();return {nB1}+{nB2}*65536 end;");
            sb.Append($"local function {nRi64}() local {nV}=0;local {nM}=1;do local _k=1;while _k<=8 do {nV}={nV}+{nRu8}()*{nM};{nM}={nM}*256;_k=_k+1 end end;return {nV} end;");

            // float64
            sb.Append($"local function {nRf64}()");
            sb.Append($"local _ba={{}};do local _k=1;while _k<=8 do _ba[_k]={nRu8}();_k=_k+1 end end;");
            sb.Append($"local {nHi},{ nLo}=0,0;");
            sb.Append($"do local _k=8;while _k>=5 do {nHi}={nHi}*256+_ba[_k];_k=_k-1 end end;");
            sb.Append($"do local _k=4;while _k>=1 do {nLo}={nLo}*256+_ba[_k];_k=_k-1 end end;");
            sb.Append($"local {nSg}=math.floor({nHi}/0x80000000);");
            sb.Append($"local {nEx2}=bit32.band(math.floor({nHi}/0x100000),0x7FF);");
            sb.Append($"local {nMn}=({nHi}%0x100000)*0x100000000+{nLo};");
            sb.Append($"if {nEx2}==0x7FF then return ({nSg}==0 and math.huge or -math.huge) end;");
            sb.Append($"if {nEx2}==0 then return math.ldexp({nMn}/0x10000000000000,-1022)*(1-2*{nSg}) end;");
            sb.Append($"return math.ldexp(({nMn}/0x10000000000000)+1,{nEx2}-1023)*({nSg}==0 and 1 or -1) end;");

            // read string
            sb.Append($"local function {nRstr}() local {nCnt}={nRu32}();if {nCnt}==0 then return nil end;");
            sb.Append($"local _t={{}};do local _k=1;while _k<={nCnt} do _t[_k]=string.char({nRu8}());_k=_k+1 end end;return table.concat(_t) end;");

            // read proto
            sb.Append($"local function {nRpro}() local _r={{}};_r.params={nRu8}();_r.isva={nRu8}()==1;_r.ms={nRu8}();");
            sb.Append($"local {nCnt}={nRu32}();_r.code={{}};");
            sb.Append($"do local _k=1;while _k<={nCnt} do local _o={nRu8}(),_a={nRu8}(),_b={nRu8}(),_c={nRu8}();local _bx=_b+_c*256;_r.code[_k]={{op=_o,a=_a,b=_b,c=_c,bx=_bx,sbx=_bx-32767}};_k=_k+1 end end;");
            sb.Append($"local {nCnt}={nRu32}();_r.consts={{}};");
            sb.Append($"do local _k=1;while _k<={nCnt} do local _ct={nRu8}();");
            sb.Append($"if _ct==0 then _r.consts[_k]={{t=0,v=nil}} elseif _ct==1 then _r.consts[_k]={{t=1,v={nRu8}()==1}} elseif _ct==2 then _r.consts[_k]={{t=2,v={nRi64}()}} elseif _ct==3 then _r.consts[_k]={{t=3,v={nRf64}()}} elseif _ct==4 then _r.consts[_k]={{t=4,v={nRstr}()}} end;");
            sb.Append($"_k=_k+1 end end;");
            sb.Append($"local {nCnt}={nRu32}();_r.uv={{}};do local _k=1;while _k<={nCnt} do _r.uv[_k]={nRstr}();_k=_k+1 end end;");
            sb.Append($"local {nCnt}={nRu32}();_r.pr={{}};do local _k=1;while _k<={nCnt} do _r.pr[_k]={nRpro}();_k=_k+1 end end;");
            sb.Append($"return _r end;");

            // magic + deserialize
            A($"local {nMag}={{0xDE,0xAD,0xBE,0xEF,0x01}}");
            sb.Append($"do local _k=1;while _k<=5 do if {nBytes}[_k]~={nMag}[_k] then error(\"Bad magic [{jv3}]\") end;_k=_k+1 end end;");
            A($"{nPos}=6");
            A($"local {nRoot}={nRpro}()");

            // more junk
            A($"local {Names.Gen()}=bit32.bxor(0x{Rng.Next(0x100,0xFFF):X3},0x{Rng.Next(0x100,0xFFF):X3})");
            A($"local {Names.Gen()}=bit32.band(0x{Rng.Next(0xFF,0xFFFF):X4},{j1})");

            // ── VM ──────────────────────────────────────────────────────────────
            A($"local {nEnv}=(getfenv and getfenv(0)) or _ENV or {{}}");
            A($"local {nExec}");
            sb.Append($"{nExec}=function({nProto},{nUpv},{nArgs})");
            A($"local {nK}={nProto}.consts");
            A($"local {nP}={nProto}.pr");
            A($"local {nStk}={{}}");
            A($"local {nIp}=1");
            sb.Append($"if {nArgs} then do local _k=1;while _k<=#{nArgs} do {nStk}[_k-1]={nArgs}[_k];_k=_k+1 end end end;");

            // kRK helper: get value from register or constant (constant index >=256)
            string nKRK=Names.Gen();
            sb.Append($"local function {nKRK}(_idx) if _idx>=256 then local _kk={nK}[_idx-255];return _kk and _kk.v end;return {nStk}[_idx] end;");

            // call helper
            sb.Append($"local function {nCall}({nFn},{nBase},{nNa},{nNr})");
            sb.Append($"if type({nFn})==\"function\" then");
            sb.Append($"local {nTa}={{}};do local _k=0;while _k<{nNa} do {nTa}[_k+1]={nStk}[{nBase}+_k];_k=_k+1 end end;");
            sb.Append($"local _rs={{({nFn})(table.unpack({nTa}))}};");
            sb.Append($"if {nNr}>0 then do local _k=0;while _k<{nNr} do {nStk}[{nBase}+_k]=_rs[_k+1];_k=_k+1 end end end;");
            sb.Append($"return _rs;");
            sb.Append($"elseif type({nFn})==\"table\" and {nFn}.__vp then");
            sb.Append($"local {nTa}={{}};do local _k=0;while _k<{nNa} do {nTa}[_k+1]={nStk}[{nBase}+_k];_k=_k+1 end end;");
            sb.Append($"local {nSr}={nExec}({nFn}.__vp,{nFn}.__uv,{nTa});");
            sb.Append($"if {nNr}>0 then do local _k=0;while _k<{nNr} do {nStk}[{nBase}+_k]={nSr}[_k+1];_k=_k+1 end end end;");
            sb.Append($"return {nSr};");
            sb.Append($"else error(\"call \"..type({nFn})) end end;");

            // main loop
            sb.Append($"while true do");
            sb.Append($"local {nInsn}={nProto}.code[{nIp}];");
            sb.Append($"if not {nInsn} then break end;");
            sb.Append($"local {nOp}={nInsn}.op;");
            sb.Append($"local {nA}={nInsn}.a;");
            sb.Append($"local {nB}={nInsn}.b;");
            sb.Append($"local {nC}={nInsn}.c;");
            sb.Append($"local {nBx}={nInsn}.bx;");
            sb.Append($"local {nSbx}={nInsn}.sbx;");
            sb.Append($"{nIp}={nIp}+1;");

            void Op_(int code, string body) =>
                sb.Append($"if {nOp}=={code} then {body} else");
            void OpEnd() => sb.Append($" end;");
            // We'll use a chained if-elseif structure

            sb.Append(
            // LOADNIL=0
            $"if {nOp}==0 then do local _k={nA};while _k<={nA}+{nB} do {nStk}[_k]=nil;_k=_k+1 end end"+
            // LOADBOOL=1
            $" elseif {nOp}==1 then {nStk}[{nA}]=({nB}~=0);if {nC}~=0 then {nIp}={nIp}+1 end"+
            // LOADK=2
            $" elseif {nOp}==2 then local _kv={nK}[{nBx}+1];{nStk}[{nA}]=_kv and _kv.v or nil"+
            // MOVE=3
            $" elseif {nOp}==3 then {nStk}[{nA}]={nStk}[{nB}]"+
            // GETGLOBAL=4
            $" elseif {nOp}==4 then local _kv={nK}[{nBx}+1];{nStk}[{nA}]={nEnv}[_kv and _kv.v or '']"+
            // SETGLOBAL=5
            $" elseif {nOp}==5 then local _kv={nK}[{nBx}+1];{nEnv}[_kv and _kv.v or '']={nStk}[{nA}]"+
            // GETTABLE=6
            $" elseif {nOp}==6 then local _tb={nStk}[{nB}];local _idx={nKRK}({nC});if _tb then {nStk}[{nA}]=_tb[_idx] else {nStk}[{nA}]=nil end"+
            // SETTABLE=7
            $" elseif {nOp}==7 then local _tb={nStk}[{nA}];local _ki={nKRK}({nB});local _vi={nKRK}({nC});if _tb then _tb[_ki]=_vi end"+
            // NEWTABLE=8
            $" elseif {nOp}==8 then {nStk}[{nA}]=[[]]"+
            // ADD=9
            $" elseif {nOp}==9 then {nStk}[{nA}]={nKRK}({nB})+{nKRK}({nC})"+
            // SUB=10
            $" elseif {nOp}==10 then {nStk}[{nA}]={nKRK}({nB})-{nKRK}({nC})"+
            // MUL=11
            $" elseif {nOp}==11 then {nStk}[{nA}]={nKRK}({nB})*{nKRK}({nC})"+
            // DIV=12
            $" elseif {nOp}==12 then {nStk}[{nA}]={nKRK}({nB})/{nKRK}({nC})"+
            // MOD=13
            $" elseif {nOp}==13 then {nStk}[{nA}]={nKRK}({nB})%{nKRK}({nC})"+
            // POW=14
            $" elseif {nOp}==14 then {nStk}[{nA}]={nKRK}({nB})^{nKRK}({nC})"+
            // CONCAT=15
            $" elseif {nOp}==15 then local _pts={{}};do local _k={nB};while _k<={nC} do _pts[#_pts+1]=tostring({nStk}[_k]);_k=_k+1 end end;{nStk}[{nA}]=table.concat(_pts)"+
            // UNM=16
            $" elseif {nOp}==16 then {nStk}[{nA}]=-{nStk}[{nB}]"+
            // NOT=17
            $" elseif {nOp}==17 then {nStk}[{nA}]=not {nStk}[{nB}]"+
            // LEN=18
            $" elseif {nOp}==18 then {nStk}[{nA}]=#{nStk}[{nB}]"+
            // EQ=19
            $" elseif {nOp}==19 then local _av={nKRK}({nB});local _bv={nKRK}({nC});if (_av==_bv)~=({nA}~=0) then {nIp}={nIp}+1 end"+
            // LT=20
            $" elseif {nOp}==20 then local _av={nKRK}({nB});local _bv={nKRK}({nC});if (_av<_bv)~=({nA}~=0) then {nIp}={nIp}+1 end"+
            // LE=21
            $" elseif {nOp}==21 then local _av={nKRK}({nB});local _bv={nKRK}({nC});if (_av<={nKRK}({nC}))~=({nA}~=0) then {nIp}={nIp}+1 end"+
            // JMP=22
            $" elseif {nOp}==22 then {nIp}={nIp}+{nSbx}"+
            // TEST=23
            $" elseif {nOp}==23 then local _tv={nStk}[{nA}];if ((_tv~=false and _tv~=nil)==({nC}~=0)) then else {nIp}={nIp}+1 end"+
            // CALL=24
            $" elseif {nOp}==24 then local _fn={nStk}[{nA}];local _na={nB}-1;local _nr={nC}-1;{nCall}(_fn,{nA}+1,_na,_nr+1)"+
            // RETURN=25
            $" elseif {nOp}==25 then local _rt={{}};if {nB}==1 then return _rt elseif {nB}==0 then do local _k={nA};while {nStk}[_k]~=nil do _rt[#_rt+1]={nStk}[_k];_k=_k+1 end end;return _rt else do local _k={nA};while _k<={nA}+{nB}-2 do _rt[#_rt+1]={nStk}[_k];_k=_k+1 end end;return _rt end"+
            // CLOSURE=26
            $" elseif {nOp}==26 then local _sp={nP}[{nBx}+1];{nStk}[{nA}]={{__vp=_sp,__uv={}}}"+
            // VARARG=27
            $" elseif {nOp}==27 then if {nArgs} then local _sz=({nB}==0 and #{nArgs} or {nB}-1);do local _k=1;while _k<=_sz do {nStk}[{nA}+_k-1]={nArgs}[_k];_k=_k+1 end end end"+
            // FORPREP=28
            $" elseif {nOp}==28 then {nStk}[{nA}]={nStk}[{nA}]-{nStk}[{nA}+2];{nIp}={nIp}+{nSbx}"+
            // FORLOOP=29
            $" elseif {nOp}==29 then {nStk}[{nA}]={nStk}[{nA}]+{nStk}[{nA}+2];if {nStk}[{nA}+2]>0 then if {nStk}[{nA}]<={nStk}[{nA}+1] then {nStk}[{nA}+3]={nStk}[{nA}];{nIp}={nIp}+{nSbx} end else if {nStk}[{nA}]>={nStk}[{nA}+1] then {nStk}[{nA}+3]={nStk}[{nA}];{nIp}={nIp}+{nSbx} end end"+
            // GETUPVAL=30
            $" elseif {nOp}==30 then {nStk}[{nA}]={nUpv} and {nUpv}[{nB}+1] or nil"+
            // SETUPVAL=31
            $" elseif {nOp}==31 then if {nUpv} then {nUpv}[{nB}+1]={nStk}[{nA}] end"+
            // SELF=32
            $" elseif {nOp}==32 then local _tb={nStk}[{nB}];local _mi={nKRK}({nC});{nStk}[{nA}+1]=_tb;{nStk}[{nA}]=_tb and _tb[_mi] or nil"+
            // BAND=33
            $" elseif {nOp}==33 then {nStk}[{nA}]=bit32.band({nKRK}({nB}),{nKRK}({nC}))"+
            // BOR=34
            $" elseif {nOp}==34 then {nStk}[{nA}]=bit32.bor({nKRK}({nB}),{nKRK}({nC}))"+
            // BXOR=35
            $" elseif {nOp}==35 then {nStk}[{nA}]=bit32.bxor({nKRK}({nB}),{nKRK}({nC}))"+
            // BNOT=36
            $" elseif {nOp}==36 then {nStk}[{nA}]=bit32.bnot({nStk}[{nB}])"+
            // IDIV=37
            $" elseif {nOp}==37 then {nStk}[{nA}]=math.floor({nKRK}({nB})/{nKRK}({nC}))"+
            // SHL=38
            $" elseif {nOp}==38 then {nStk}[{nA}]=bit32.lshift({nKRK}({nB}),{nKRK}({nC}))"+
            // SHR=39
            $" elseif {nOp}==39 then {nStk}[{nA}]=bit32.rshift({nKRK}({nB}),{nKRK}({nC}))"+
            $" else error(\"op \"..tostring({nOp})) end"
            );

            sb.Append(" end;"); // end while true
            sb.Append($"return {{}} end;"); // end exec function

            // ── entry point ─────────────────────────────────────────────────────
            // fix NEWTABLE=8 - we wrote [[]] which is wrong in the op body, it should be {}
            // We'll fix by replacing the literal. Actually in the string building above
            // NEWTABLE used [[]] as a hack - let's correct it:
            // (we cannot use {} inside $"..." without escaping to {{}}
            // The line was: $"... elseif {nOp}==8 then {nStk}[{nA}]=[[]]..."
            // [[]] is valid Lua - it's a long string, not a table. We need to fix this.
            // The fix: use a helper function or construct differently.
            // We'll patch by using a dedicated helper.
            string nNewTab=Names.Gen();
            // insert newTable helper before exec
            // (We add it to sb - but exec is already written. We'll prefix it.)
            string vmCode = sb.ToString();
            // Replace [[]] with the correct table constructor via a helper
            string newTabHelper=$"local function {nNewTab}() return {{}} end;";
            vmCode=vmCode.Replace($"{nStk}[{nA}]=[[]]",$"{nStk}[{nA}]={nNewTab}()");

            // ── entry ────────────────────────────────────────────────────────────
            string nIa2=Names.Gen(),nRs2=Names.Gen();
            string entry=
                $"local {nIa2}={{...}};"+
                $"local {nRs2}={nExec}({nRoot},nil,{nIa2});"+
                $"if {nRs2} and #{nRs2}>0 then return table.unpack({nRs2}) end;"+
                // randomized ending style
                (Rng.Next(3)==0 ?
                    $"end)(...)--[[CustomVM seal 0x{Rng.Next(0,0xFFFFFF):X6}]]" :
                 Rng.Next(2)==0 ?
                    $"end)(...) --[=[Protected]=]" :
                    $"end)(...)" );

            // prefix the newTable helper
            return "-- This file was protected using CustomVM Obfuscator v2.3 [https://example.com/]\n"+
                   newTabHelper+
                   vmCode.Substring(vmCode.IndexOf("return (function")-1>=0?
                       vmCode.IndexOf("return (function"):0)+
                   entry;

            void A(string line) { sb.Append(line+";"); }
        }

        // dummy to satisfy BuildFinal ref to nBase
        string nBase="";
    }

    class Program
    {
        static void Main(string[] args)
        {
            string src = args.Length>0 && File.Exists(args[0])
                ? File.ReadAllText(args[0])
                : "print(\"Hello from CustomVM!\")\nlocal x=1+2\nprint(x)";

            Console.WriteLine("[*] Compiling...");
            var c=new Compiler();
            Proto proto;
            try { proto=c.Compile(src); }
            catch(Exception ex){ Console.Error.WriteLine("Compile error: "+ex.Message); return; }

            Console.WriteLine("[*] Serializing...");
            var s=new Serializer();
            var bc=s.Serialize(proto);
            Console.WriteLine($"[*] Bytecode: {bc.Length} bytes");

            var crc=CRC.Calc(bc);
            Console.WriteLine($"[*] CRC32: 0x{crc:X8}");

            var z85=Z85.Encode(bc);
            Console.WriteLine($"[*] Z85: {z85.Length} chars");

            Console.WriteLine("[*] Generating VM...");
            var gen=new VMGen();
            var output=gen.Generate(z85,crc);

            string outPath=args.Length>1?args[1]:"obfuscated.lua";
            File.WriteAllText(outPath,output,Encoding.UTF8);
            Console.WriteLine($"[*] Written to {outPath} ({output.Length} bytes)");
        }
    }
}
