namespace Mixamp.Core;

public record Change(Track Track, string Type, string From, string To, string Before, string After,
    string SourcePath = "", string TargetPath = "")
{
    public Change Reverse() => Type switch
    {
        "Move" => this with { From = To, To = From, Before = After, After = Before,
            SourcePath = TargetPath, TargetPath = SourcePath },
        "Tags" => this with { Before = After, After = Before, To = TargetPath.Length == 0 ? To + ".redo" : TargetPath, TargetPath = To },
        _ => this with { Before = After, After = Before }
    };
}
public record UndoStep(long Id, string Name, Change Change);
