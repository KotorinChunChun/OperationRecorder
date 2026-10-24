using System.Collections.Generic;

namespace OpRec
{
    // 画像等は作業フォルダーに保存し、JSONには編集内容と安定した出典だけを保持する。
    public sealed class CompositionSource
    {
        public string Id { get; set; }
        public string Caption { get; set; }
        public string OriginalDirectory { get; set; }
    }

    public sealed class CompositionReference
    {
        public string SourceId { get; set; }
        public long Seq { get; set; }
    }

    public sealed class CompositionRow
    {
        public string Id { get; set; }
        public bool Included { get; set; } = true;
        public List<CompositionReference> Candidates { get; set; } = new List<CompositionReference>();
        public int SelectedCandidate { get; set; }
        public string HeadlineOverride { get; set; }
        public string BodyOverride { get; set; }
    }
}
