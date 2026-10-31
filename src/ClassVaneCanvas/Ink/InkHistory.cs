using ClassVaneCanvas.Models.Annotations;
using ClassVaneCanvas.Models.Ink;

namespace ClassVaneCanvas.Ink;

/// <summary>可撤销的批注编辑（命令模式）。</summary>
public interface IInkEdit
{
    /// <summary>中文描述，用于撤销/重做提示。</summary>
    string Description { get; }

    /// <summary>执行（重做）。</summary>
    void Apply(AnnotationDocument document);

    /// <summary>撤销。</summary>
    void Revert(AnnotationDocument document);
}

/// <summary>新增一条笔迹。</summary>
public sealed class AddStrokeEdit : IInkEdit
{
    private readonly int _pageIndex;
    private readonly InkStroke _stroke;

    public AddStrokeEdit(int pageIndex, InkStroke stroke)
    {
        _pageIndex = pageIndex;
        _stroke = stroke;
    }

    public string Description => "书写";

    public void Apply(AnnotationDocument document) => document.GetOrCreatePage(_pageIndex).Strokes.Add(_stroke);

    public void Revert(AnnotationDocument document)
        => document.FindPage(_pageIndex)?.Strokes.RemoveAll(s => s.Id == _stroke.Id);
}

/// <summary>擦除若干条笔迹（记录原始位置以便精确还原）。</summary>
public sealed class EraseStrokesEdit : IInkEdit
{
    private readonly int _pageIndex;
    private readonly List<(int Index, InkStroke Stroke)> _removed;

    public EraseStrokesEdit(int pageIndex, IEnumerable<(int Index, InkStroke Stroke)> removed)
    {
        _pageIndex = pageIndex;
        _removed = removed.OrderBy(r => r.Index).ToList();
    }

    public string Description => _removed.Count > 1 ? $"擦除 {_removed.Count} 处笔迹" : "擦除笔迹";

    public int Count => _removed.Count;

    public void Apply(AnnotationDocument document)
    {
        var page = document.FindPage(_pageIndex);
        if (page is null)
        {
            return;
        }

        var ids = _removed.Select(r => r.Stroke.Id).ToHashSet(StringComparer.Ordinal);
        page.Strokes.RemoveAll(s => ids.Contains(s.Id));
    }

    public void Revert(AnnotationDocument document)
    {
        var page = document.GetOrCreatePage(_pageIndex);

        // 按下标升序插回，恢复原有层叠顺序
        foreach (var (index, stroke) in _removed)
        {
            var target = Math.Clamp(index, 0, page.Strokes.Count);
            page.Strokes.Insert(target, stroke);
        }
    }
}

/// <summary>清空整页批注。</summary>
public sealed class ClearPageEdit : IInkEdit
{
    private readonly int _pageIndex;
    private readonly List<InkStroke> _snapshot;

    public ClearPageEdit(int pageIndex, IEnumerable<InkStroke> snapshot)
    {
        _pageIndex = pageIndex;
        _snapshot = snapshot.ToList();
    }

    public string Description => "清空本页批注";

    public void Apply(AnnotationDocument document)
        => document.FindPage(_pageIndex)?.Strokes.Clear();

    public void Revert(AnnotationDocument document)
    {
        var page = document.GetOrCreatePage(_pageIndex);
        page.Strokes.Clear();
        page.Strokes.AddRange(_snapshot);
    }
}

/// <summary>
/// 撤销 / 重做栈。
/// 约定：调用方先把编辑应用到文档，再用 <see cref="Record"/> 入栈（记录已应用的编辑）。
/// </summary>
public sealed class InkHistory
{
    /// <summary>历史深度上限，避免长时间上课占用内存。</summary>
    public const int MaxDepth = 200;

    private readonly List<IInkEdit> _undo = new();
    private readonly List<IInkEdit> _redo = new();

    /// <summary>栈内容变化通知（用于刷新按钮可用状态）。</summary>
    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    /// <summary>下一次撤销的描述。</summary>
    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

    /// <summary>下一次重做的描述。</summary>
    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    /// <summary>记录一个**已经应用**的编辑。新编辑会使重做栈失效。</summary>
    public void Record(IInkEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        _undo.Add(edit);
        if (_undo.Count > MaxDepth)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>撤销一步。</summary>
    public bool Undo(AnnotationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (_undo.Count == 0)
        {
            return false;
        }

        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        edit.Revert(document);
        _redo.Add(edit);

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>重做一步。</summary>
    public bool Redo(AnnotationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (_redo.Count == 0)
        {
            return false;
        }

        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        edit.Apply(document);
        _undo.Add(edit);

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>清空历史（切换课件时调用）。</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
