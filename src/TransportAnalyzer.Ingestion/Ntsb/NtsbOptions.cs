namespace TransportAnalyzer.Ingestion.Ntsb;

/// <summary>Bound from configuration section <c>Sources:ntsb</c>.</summary>
public sealed class NtsbOptions
{
    /// <summary>Download URL of the avall.zip database (a single Access MDB, 2008 onwards).</summary>
    public string Url { get; set; } = "https://data.ntsb.gov/avdata/FileDirectory/DownloadFile?fileID=C%3A%5Cavdata%5Cavall.zip";

    /// <summary>Name of the snapshot: the raw file is saved as <c>{Snapshot}.zip</c> and every staged row carries it in
    /// the <c>snapshot</c> column. A run replaces the staged rows of its snapshot.</summary>
    public string Snapshot { get; set; } = "avall";
}
