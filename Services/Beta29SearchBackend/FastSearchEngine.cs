// Port of the effective Beta 29 Add-Type source.
// Keep the two Camel replacements as a single space: PowerShell expands $1 and $2.
using Beta29.SearchBackend.Compatibility;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Beta29.SearchBackend;

public sealed class FastSearchEntry
{
    public object Package { get; set; }
    public string Name { get; set; }
    public string Id { get; set; }
    public string Version { get; set; }
    public string Publisher { get; set; }
    public string ShortDescription { get; set; }
    public string NameN { get; set; }
    public string IdN { get; set; }
    public string MonikerN { get; set; }
    public string PublisherN { get; set; }
    public string ShortN { get; set; }
    public string DescriptionN { get; set; }
    public string TagsN { get; set; }
    public string IdLeafN { get; set; }
    public string AllTextN { get; set; }
    public string NameExactN { get; set; }
    public string IdExactN { get; set; }
    public string MonikerExactN { get; set; }
    public string IdLeafExactN { get; set; }
}

public sealed class FastScoredEntry
{
    public double Score { get; set; }
    public double Bm25Score { get; set; }
    public double PhraseBonus { get; set; }
    public double IdentityBonus { get; set; }
    public double IdentityFactor { get; set; }
    public double RootFactor { get; set; }
    public double SameFieldFactor { get; set; }
    public double CoordinationFactor { get; set; }
    public double QualityFactor { get; set; }
    public int MatchClass { get; set; }
    public int CoveredWords { get; set; }
    public int StrongCoveredWords { get; set; }
    public bool ExactIdentity { get; set; }
    public string MatchDetails { get; set; }
    public FastSearchEntry Entry { get; set; }
}

public static class FastSearchEngine
{
    // Beta 28: C# port of the relevant MiniSearch lexical search pipeline.
    // MiniSearch core semantics retained here: inverted index, per-field postings,
    // BM25+ defaults, OR term combination, field boosts, prefix weighting, and
    // final quality multiplier = number of distinct matched query terms.
    private const double K = 1.2;
    private const double B = 0.7;
    private const double D = 0.5;
    private const double PrefixWeight = 0.375;

    private static readonly Regex Splitter = new Regex(FrameworkUnicode8.SplitPattern, RegexOptions.Compiled);
    private static readonly Regex Camel1 = new Regex(@"([a-z0-9])([A-Z])", RegexOptions.Compiled);
    private static readonly Regex Camel2 = new Regex(@"([A-Z]+)([A-Z][a-z])", RegexOptions.Compiled);

    private static readonly string[] Fields = new[]{"Name","Id","Moniker","Tags","Short","Publisher","Description"};
    private static readonly double[] Boosts = new[]{6.0,5.0,5.0,3.5,3.0,1.0,0.65};
    private static readonly Dictionary<string,int> FieldIds = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase) {
        {"Name",0},{"Id",1},{"Moniker",2},{"Tags",3},{"Short",4},{"Publisher",5},{"Description",6}
    };

    // term -> fieldId -> docId -> term frequency (same shape as MiniSearch index)
    private static readonly Dictionary<string,Dictionary<int,Dictionary<int,int>>> Index =
        new Dictionary<string,Dictionary<int,Dictionary<int,int>>>(StringComparer.Ordinal);
    private static readonly List<FastSearchEntry> Documents = new List<FastSearchEntry>();
    private static readonly Dictionary<string,FastSearchEntry> DocumentsByRawId =
        new Dictionary<string,FastSearchEntry>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<int[]> FieldLengths = new List<int[]>();
    private static readonly double[] AvgFieldLength = new double[7];

    private sealed class RawResult
    {
        public double Score;
        public HashSet<string> QueryTerms = new HashSet<string>(StringComparer.Ordinal);
        public Dictionary<string,HashSet<string>> Match = new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);
        public HashSet<string> PrefixTerms = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> FuzzyTerms = new HashSet<string>(StringComparer.Ordinal);
    }

    public static string NormalizeText(object value)
    {
        if(value==null) return String.Empty;
        string text=FrameworkNumber.ConvertToString(value);
        if(String.IsNullOrWhiteSpace(text)) return String.Empty;
        string[] parts=Splitter.Split(text.Normalize(NormalizationForm.FormKC));
        List<string> outp=new List<string>();
        foreach(string p in parts) if(!String.IsNullOrWhiteSpace(p)) outp.Add(p.ToLowerInvariant());
        return String.Join(" ",outp.ToArray());
    }

    private static List<string> BaseTokens(object value)
    {
        List<string> result=new List<string>();
        if(value==null) return result;
        string text=FrameworkNumber.ConvertToString(value);
        if(String.IsNullOrWhiteSpace(text)) return result;
        foreach(string p in Splitter.Split(text.Normalize(NormalizationForm.FormKC)))
            if(!String.IsNullOrWhiteSpace(p)) result.Add(p.ToLowerInvariant());
        return result;
    }

    // PARITY NOTE: the original comments below describe the author's intent.
    // In the actual PowerShell here-string "$1 $2" expands to " ", so the
    // matched boundary characters are removed. Do not repair the replacements.
    // Thin Winget preprocessing layer. MiniSearch explicitly supports a custom
    // tokenizer/processTerm pipeline. Identity-like fields additionally expose
    // CamelCase/acronym components while retaining the original token. Thus
    // LibreOffice indexes libreoffice + libre + office, and ALCPU.CoreTemp exposes
    // alcpu + cpu + coretemp + core + temp, without arbitrary substring matching.
    private static List<string> IdentityTokens(object value)
    {
        List<string> result=new List<string>();
        if(value==null) return result;
        string text=FrameworkNumber.ConvertToString(value);
        if(String.IsNullOrWhiteSpace(text)) return result;
        string[] chunks=Splitter.Split(text.Normalize(NormalizationForm.FormKC));
        foreach(string chunk in chunks)
        {
            if(String.IsNullOrWhiteSpace(chunk)) continue;
            string whole=chunk.ToLowerInvariant();
            if(!result.Contains(whole)) result.Add(whole);
            string split=Camel2.Replace(chunk," ");
            split=Camel1.Replace(split," ");
            foreach(string part in split.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries))
            {
                string n=part.ToLowerInvariant();
                if(!result.Contains(n)) result.Add(n);
                // Acronym-bearing identifier components such as ALCPU are common
                // in Winget IDs. Expose a trailing 3-letter acronym only for an
                // all-uppercase component; this is preprocessing, not substring search.
                bool allCaps=true;
                for(int i=0;i<part.Length;i++) if(FrameworkUnicode8.IsLetter(part[i]) && !FrameworkUnicode8.IsUpper(part[i])) { allCaps=false; break; }
                if(allCaps && part.Length>4) {
                    string suffix=part.Substring(part.Length-3).ToLowerInvariant();
                    if(!result.Contains(suffix)) result.Add(suffix);
                }
            }
        }
        return result;
    }

    private static string JoinTokens(List<string> tokens) { return String.Join(" ",tokens.ToArray()); }
    private static string ExactNormalize(object value) { return NormalizeText(value).Trim(); }
    private static string NormalizeTags(object tags)
    {
        if(tags==null) return String.Empty;
        IEnumerable en=tags as IEnumerable;
        if(en==null || tags is string) return NormalizeText(tags);
        List<string> all=new List<string>();
        foreach(object tag in en) all.AddRange(BaseTokens(tag));
        return JoinTokens(all);
    }

    public static FastSearchEntry BuildEntry(object package, object nameValue, object idValue,
        object versionValue, object publisherValue, object shortValue, object monikerValue,
        object descriptionValue, object tagsValue)
    {
        string name=nameValue==null?String.Empty:FrameworkNumber.ConvertToString(nameValue);
        string id=idValue==null?String.Empty:FrameworkNumber.ConvertToString(idValue);
        string version=versionValue==null?String.Empty:FrameworkNumber.ConvertToString(versionValue);
        string publisher=publisherValue==null?String.Empty:FrameworkNumber.ConvertToString(publisherValue);
        string shortDescription=shortValue==null?String.Empty:FrameworkNumber.ConvertToString(shortValue);
        string nameN=JoinTokens(IdentityTokens(nameValue));
        string idN=JoinTokens(IdentityTokens(idValue));
        string monikerN=JoinTokens(IdentityTokens(monikerValue));
        string publisherN=NormalizeText(publisherValue);
        string shortN=NormalizeText(shortValue);
        string descriptionN=NormalizeText(descriptionValue);
        string tagsN=NormalizeTags(tagsValue);
        string idLeafN=String.Empty;
        if(id.Length>0) { int dot=id.LastIndexOf('.'); idLeafN=JoinTokens(IdentityTokens(dot>=0?id.Substring(dot+1):id)); }
        return new FastSearchEntry { Package=package,Name=name,Id=id,Version=version,Publisher=publisher,
            ShortDescription=shortDescription,NameN=nameN,IdN=idN,MonikerN=monikerN,PublisherN=publisherN,
            ShortN=shortN,DescriptionN=descriptionN,TagsN=tagsN,IdLeafN=idLeafN,
            AllTextN=String.Join(" ",new[]{nameN,idN,monikerN,publisherN,shortN,tagsN,descriptionN}),
            NameExactN=ExactNormalize(nameValue),IdExactN=ExactNormalize(idValue),MonikerExactN=ExactNormalize(monikerValue),
            IdLeafExactN=ExactNormalize(id.Length>0?(id.LastIndexOf('.')>=0?id.Substring(id.LastIndexOf('.')+1):id):String.Empty) };
    }

    private static string[] Tokens(string text) { return String.IsNullOrEmpty(text)?new string[0]:text.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries); }
    private static string FieldText(FastSearchEntry e,int fieldId)
    {
        switch(fieldId) { case 0:return e.NameN; case 1:return e.IdN; case 2:return e.MonikerN; case 3:return e.TagsN;
            case 4:return e.ShortN; case 5:return e.PublisherN; default:return e.DescriptionN; }
    }
    private static void AddTerm(string term,int fieldId,int docId)
    {
        Dictionary<int,Dictionary<int,int>> byField;
        if(!Index.TryGetValue(term,out byField)) { byField=new Dictionary<int,Dictionary<int,int>>(); Index[term]=byField; }
        Dictionary<int,int> postings;
        if(!byField.TryGetValue(fieldId,out postings)) { postings=new Dictionary<int,int>(); byField[fieldId]=postings; }
        int tf; postings.TryGetValue(docId,out tf); postings[docId]=tf+1;
    }

    public static void PrepareIndex(IEnumerable<FastSearchEntry> entries)
    {
        Index.Clear(); Documents.Clear(); DocumentsByRawId.Clear(); FieldLengths.Clear(); Array.Clear(AvgFieldLength,0,AvgFieldLength.Length);
        if(entries==null) return;
        foreach(FastSearchEntry e in entries)
        {
            if(e==null) continue; int docId=Documents.Count; Documents.Add(e);
            if(!String.IsNullOrWhiteSpace(e.Id) && !DocumentsByRawId.ContainsKey(e.Id)) DocumentsByRawId[e.Id]=e;
            int[] lengths=new int[7];
            for(int f=0;f<7;f++) {
                string[] ts=Tokens(FieldText(e,f)); lengths[f]=ts.Length;
                foreach(string t in ts) AddTerm(t,f,docId);
                AvgFieldLength[f]+=lengths[f];
            }
            FieldLengths.Add(lengths);
        }
        double n=Math.Max(1,Documents.Count); for(int f=0;f<7;f++) AvgFieldLength[f]/=n;
    }

    private static double BM25(int tf,int matchingCount,int totalCount,int fieldLength,double avgLength)
    {
        double idf=Math.Log(1.0+(totalCount-matchingCount+0.5)/(matchingCount+0.5));
        double avg=Math.Max(1e-9,avgLength);
        return idf*(D + tf*(K+1.0)/(tf+K*(1.0-B+B*fieldLength/avg)));
    }
    private static void AddMatch(RawResult r,string sourceTerm,string derivedTerm,string field,bool prefix,bool fuzzy)
    {
        r.QueryTerms.Add(sourceTerm); HashSet<string> fs;
        if(!r.Match.TryGetValue(derivedTerm,out fs)) { fs=new HashSet<string>(StringComparer.Ordinal); r.Match[derivedTerm]=fs; }
        fs.Add(field); if(prefix) r.PrefixTerms.Add(derivedTerm); if(fuzzy) r.FuzzyTerms.Add(derivedTerm);
    }
    private static void AddTermResults(Dictionary<int,RawResult> results,string sourceTerm,string derivedTerm,double termWeight,bool prefix,bool fuzzy)
    {
        Dictionary<int,Dictionary<int,int>> byField; if(!Index.TryGetValue(derivedTerm,out byField)) return;
        for(int f=0;f<7;f++) {
            Dictionary<int,int> postings; if(!byField.TryGetValue(f,out postings)) continue;
            int matchingCount=postings.Count;
            foreach(KeyValuePair<int,int> p in postings) {
                double raw=BM25(p.Value,matchingCount,Documents.Count,FieldLengths[p.Key][f],AvgFieldLength[f]);
                double weighted=termWeight*Boosts[f]*raw; RawResult rr;
                if(!results.TryGetValue(p.Key,out rr)) { rr=new RawResult(); results[p.Key]=rr; }
                rr.Score+=weighted; AddMatch(rr,sourceTerm,derivedTerm,Fields[f],prefix,fuzzy);
            }
        }
    }
    private static int Levenshtein(string a,string b)
    {
        if(a==b)return 0;if(a.Length==0)return b.Length;if(b.Length==0)return a.Length;
        int[] prev=new int[b.Length+1],cur=new int[b.Length+1];for(int j=0;j<=b.Length;j++)prev[j]=j;
        for(int i=1;i<=a.Length;i++){cur[0]=i;for(int j=1;j<=b.Length;j++){int cost=a[i-1]==b[j-1]?0:1;cur[j]=Math.Min(Math.Min(cur[j-1]+1,prev[j]+1),prev[j-1]+cost);}int[] t=prev;prev=cur;cur=t;}return prev[b.Length];
    }
    private static List<string> QueryTerms(string query)
    {
        List<string> terms=BaseTokens(query); List<string> unique=new List<string>(); foreach(string t in terms) if(!unique.Contains(t)) unique.Add(t); return unique;
    }
    private static bool PrefixEnabled(string term) { return term!=null && term.Length>3; }
    private static bool FuzzyEnabled(string term) { return false; } // Beta 26: fuzzy intentionally disabled; exact/prefix behavior stays deterministic.

    private static bool ExistingAncestor(FastSearchEntry e, out FastSearchEntry ancestor, out string familyRoot, out string familySuffix)
    {
        ancestor=null; familyRoot=String.Empty; familySuffix=String.Empty;
        if(e==null || String.IsNullOrWhiteSpace(e.Id)) return false;

        // IMPORTANT: family structure must use the RAW Winget PackageIdentifier.
        // Normalized IdExactN intentionally removes punctuation, so it cannot be used
        // to discover parent IDs such as NVAccess.NVDA -> NVAccess.NVDA.Beta.
        string id=e.Id;
        while(true) {
            int dot=id.LastIndexOf('.');
            if(dot<=0) return false;
            string candidate=id.Substring(0,dot);
            FastSearchEntry found;
            if(DocumentsByRawId.TryGetValue(candidate,out found)) {
                ancestor=found;
                familyRoot=candidate;
                familySuffix=e.Id.Length>candidate.Length+1 ? e.Id.Substring(candidate.Length+1) : String.Empty;
                return true;
            }
            id=candidate;
        }
    }
    private static bool DirectIdentity(FastSearchEntry e,string q,bool includeLeaf)
    {
        if(e==null || String.IsNullOrEmpty(q)) return false;
        if(e.NameExactN==q || e.MonikerExactN==q || e.IdExactN==q) return true;
        return includeLeaf && e.IdLeafExactN==q;
    }
    private static bool IdentityFieldContainsTerm(string fieldText,string q)
    {
        if(String.IsNullOrWhiteSpace(fieldText) || String.IsNullOrWhiteSpace(q)) return false;
        foreach(string token in Tokens(fieldText)) if(token==q) return true;
        return false;
    }
    private static double IdentityFactorFor(FastSearchEntry e,string q,bool hasAncestor)
    {
        // Package ID leaf remains searchable through the boosted Id field, but it is
        // not treated as an identity signal and receives no extra multiplier.
        if(e.NameExactN==q || e.MonikerExactN==q || e.IdExactN==q) return 4.0;
        return 1.0;
    }
    private static bool VariantExplicitInQuery(string suffix,List<string> terms)
    {
        if(String.IsNullOrWhiteSpace(suffix) || terms==null || terms.Count==0) return false;
        List<string> suffixTerms=BaseTokens(suffix);
        if(suffixTerms.Count==0) return false;
        foreach(string st in suffixTerms) if(!terms.Contains(st)) return false;
        return true;
    }
    private static double RootFactorFor(FastSearchEntry ancestor,string q,bool variantExplicit)
    {
        // Demote a descendant only for a generic query that directly identifies
        // an actually present ancestor. Explicit variant queries remain undemoted.
        if(!variantExplicit && ancestor!=null && DirectIdentity(ancestor,q,true)) return 0.55;
        return 1.0;
    }
    private static bool SourceTermInField(RawResult rr,string sourceTerm,string field)
    {
        foreach(KeyValuePair<string,HashSet<string>> m in rr.Match) {
            bool belongs=(m.Key==sourceTerm) || (rr.PrefixTerms.Contains(m.Key) && m.Key.StartsWith(sourceTerm,StringComparison.Ordinal));
            if(belongs && m.Value.Contains(field)) return true;
        }
        return false;
    }
    private static double SameFieldFactorFor(RawResult rr,List<string> terms)
    {
        if(terms==null || terms.Count<2) return 1.0;
        string[] fields=new[]{"Name","Moniker","Id","Tags","Short","Description","Publisher"};
        double[] factors=new[]{1.50,1.45,1.35,1.25,1.20,1.05,1.00};
        double best=1.0;
        for(int i=0;i<fields.Length;i++) {
            bool all=true; foreach(string t in terms) if(!SourceTermInField(rr,t,fields[i])) { all=false; break; }
            if(all && factors[i]>best) best=factors[i];
        }
        return best;
    }

    public static List<FastScoredEntry> ScoreAll(IEnumerable<FastSearchEntry> ignored,string q,string[] ignoredWords)
    {
        List<FastScoredEntry> output=new List<FastScoredEntry>(); List<string> terms=QueryTerms(q); if(terms.Count==0)return output;
        Dictionary<int,RawResult> results=new Dictionary<int,RawResult>();
        foreach(string sourceTerm in terms)
        {
            AddTermResults(results,sourceTerm,sourceTerm,1.0,false,false);
            if(PrefixEnabled(sourceTerm)) {
                foreach(string candidate in Index.Keys) {
                    if(candidate==sourceTerm || !candidate.StartsWith(sourceTerm,StringComparison.Ordinal)) continue;
                    int distance=candidate.Length-sourceTerm.Length;
                    double weight=PrefixWeight*candidate.Length/(candidate.Length+0.3*distance);
                    AddTermResults(results,sourceTerm,candidate,weight,true,false);
                }
            }
        }
        foreach(KeyValuePair<int,RawResult> kv in results)
        {
            RawResult rr=kv.Value; int quality=Math.Max(1,rr.QueryTerms.Count); FastSearchEntry e=Documents[kv.Key];
            // Beta 26: MiniSearch score stays intact; Winget-specific evidence is applied
            // only as bounded multiplicative factors. No package names or locale codes are hard-coded.
            FastSearchEntry ancestor; string familyRoot; string familySuffix;
            bool hasAncestor=ExistingAncestor(e,out ancestor,out familyRoot,out familySuffix);
            bool variantExplicit=VariantExplicitInQuery(familySuffix,terms);
            double identityFactor=IdentityFactorFor(e,q,hasAncestor);
            double rootFactor=RootFactorFor(ancestor,q,variantExplicit);
            double sameFieldFactor=SameFieldFactorFor(rr,terms);
            bool exactIdentity=(e.NameExactN==q || e.IdExactN==q || e.MonikerExactN==q);
            double miniSearchScore=rr.Score*quality;
            double finalScore=miniSearchScore*identityFactor*rootFactor*sameFieldFactor;
            StringBuilder det=new StringBuilder(); bool first=true;
            foreach(string qt in terms) {
                if(!first)det.Append("; ");first=false;det.Append(qt).Append("="); List<string> found=new List<string>();
                foreach(KeyValuePair<string,HashSet<string>> m in rr.Match) {
                    bool belongs=(m.Key==qt) || (rr.PrefixTerms.Contains(m.Key) && m.Key.StartsWith(qt,StringComparison.Ordinal)) || rr.FuzzyTerms.Contains(m.Key);
                    if(!belongs)continue; foreach(string f in m.Value) if(!found.Contains(f))found.Add(f);
                }
                det.Append(found.Count==0?"-":String.Join("+",found.ToArray()));
            }
            if(rr.PrefixTerms.Count>0)det.Append(" | prefix=").Append(String.Join(",",new List<string>(rr.PrefixTerms).ToArray()));
            int strong=0; foreach(string qt in rr.QueryTerms) {
                bool isStrong=false; foreach(KeyValuePair<string,HashSet<string>> m in rr.Match) if(m.Key==qt || m.Key.StartsWith(qt,StringComparison.Ordinal))
                    if(m.Value.Contains("Name")||m.Value.Contains("Id")||m.Value.Contains("Moniker")||m.Value.Contains("Tags")||m.Value.Contains("Short")){isStrong=true;break;} if(isStrong)strong++;
            }
            det.Append(" | miniSearch=").Append(FrameworkNumber.FormatFixed(miniSearchScore,4));
            det.Append(" | identityFactor=").Append(FrameworkNumber.FormatFixed(identityFactor,2));
            det.Append(" | rootFactor=").Append(FrameworkNumber.FormatFixed(rootFactor,2));
            det.Append(" | sameFieldFactor=").Append(FrameworkNumber.FormatFixed(sameFieldFactor,2));
            det.Append(" | familyRoot=").Append(String.IsNullOrEmpty(familyRoot)?"-":familyRoot);
            det.Append(" | familySuffix=").Append(String.IsNullOrEmpty(familySuffix)?"-":familySuffix);
            det.Append(" | rootPresent=").Append(hasAncestor?"True":"False");
            det.Append(" | variantExplicit=").Append(variantExplicit?"True":"False");
            output.Add(new FastScoredEntry { Score=finalScore,Bm25Score=rr.Score,PhraseBonus=0,IdentityBonus=0,
                IdentityFactor=identityFactor,RootFactor=rootFactor,SameFieldFactor=sameFieldFactor,CoordinationFactor=quality,QualityFactor=quality,
                MatchClass=0,CoveredWords=rr.QueryTerms.Count,StrongCoveredWords=strong,ExactIdentity=exactIdentity,MatchDetails=det.ToString(),Entry=e });
        }
        return output;
    }
}
