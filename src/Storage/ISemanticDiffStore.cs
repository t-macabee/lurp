namespace Lurp.Storage
{
    public interface ISemanticDiffStore
    {
        void SaveSemanticChanges(string fromSnapshotId, string toSnapshotId, IEnumerable<SemanticChange> changes);
        List<SemanticChange> GetSemanticChanges(string fromSnapshotId, string toSnapshotId);
    }
}
