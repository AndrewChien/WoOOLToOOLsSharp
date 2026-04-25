using System;
using System.Collections.Generic;
using WoOOLToOOLsSharp.Shared.Formats.Nmp;

namespace WoOOLToOOLsSharp.MapEditor.App;

public interface IUndoableAction
{
    string Name { get; }
    void Undo();
    void Redo();
}

public sealed class UndoRedoStack
{
    private readonly List<IUndoableAction> _undo = new();
    private readonly List<IUndoableAction> _redo = new();

    private bool _hasSavePoint = true;
    private int _savePointUndoCount;

    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    public string PeekUndoName => _undo.Count > 0 ? _undo[^1].Name : string.Empty;
    public string PeekRedoName => _redo.Count > 0 ? _redo[^1].Name : string.Empty;

    public int MaxItems { get; set; } = 512;

    public bool IsAtSavePoint => _hasSavePoint && _undo.Count == _savePointUndoCount;

    public void MarkSaved()
    {
        _savePointUndoCount = _undo.Count;
        _hasSavePoint = true;
    }

    /// <summary>
    /// Mark the stack as "unsaved" even if there are no undo items.
    /// Used by new in-memory documents so the tab shows '*' until the first save.
    /// </summary>
    public void MarkUnsaved()
    {
        _savePointUndoCount = 0;
        _hasSavePoint = false;
    }

    public void Clear()
    {
        bool keepSavePoint = IsAtSavePoint;
        _undo.Clear();
        _redo.Clear();
        if (keepSavePoint)
        {
            _savePointUndoCount = 0;
            _hasSavePoint = true;
        }
        else
        {
            _savePointUndoCount = 0;
            _hasSavePoint = false;
        }
    }

    public void Push(IUndoableAction action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));

        _undo.Add(action);
        _redo.Clear();

        if (_undo.Count > MaxItems && MaxItems > 0)
        {
            int removeCount = _undo.Count - MaxItems;
            _undo.RemoveRange(0, removeCount);

            if (_hasSavePoint)
            {
                if (_savePointUndoCount < removeCount)
                {
                    _hasSavePoint = false;
                    _savePointUndoCount = 0;
                }
                else
                {
                    _savePointUndoCount -= removeCount;
                }
            }
        }
    }

    public bool TryUndo(out string actionName, out string error)
    {
        actionName = string.Empty;
        error = string.Empty;

        if (_undo.Count <= 0)
        {
            return false;
        }

        IUndoableAction action = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        try
        {
            action.Undo();
        }
        catch (Exception ex)
        {
            error = $"撤销失败: {ex.Message}";
            return false;
        }

        _redo.Add(action);
        actionName = action.Name;
        return true;
    }

    public bool TryRedo(out string actionName, out string error)
    {
        actionName = string.Empty;
        error = string.Empty;

        if (_redo.Count <= 0)
        {
            return false;
        }

        IUndoableAction action = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        try
        {
            action.Redo();
        }
        catch (Exception ex)
        {
            error = $"重做失败: {ex.Message}";
            return false;
        }

        _undo.Add(action);
        actionName = action.Name;
        return true;
    }
}

public sealed class MultiCellEditAction : IUndoableAction
{
    private readonly MapDocument _map;
    private readonly int[] _indices;
    private readonly NmpCellData[] _before;
    private readonly NmpCellData[] _after;

    public string Name { get; }

    public MultiCellEditAction(
        string name,
        MapDocument map,
        int[] indices,
        NmpCellData[] before,
        NmpCellData[] after)
    {
        Name = name ?? string.Empty;
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _indices = indices ?? throw new ArgumentNullException(nameof(indices));
        _before = before ?? throw new ArgumentNullException(nameof(before));
        _after = after ?? throw new ArgumentNullException(nameof(after));

        if (_indices.Length != _before.Length || _indices.Length != _after.Length)
        {
            throw new ArgumentException("Undo/Redo arrays length mismatch.");
        }
    }

    public void Undo()
    {
        for (int i = 0; i < _indices.Length; i++)
        {
            int index = _indices[i];
            if ((uint)index >= (uint)_map.Cells.Length)
            {
                continue;
            }

            _map.Cells[index] = _before[i];
        }
    }

    public void Redo()
    {
        for (int i = 0; i < _indices.Length; i++)
        {
            int index = _indices[i];
            if ((uint)index >= (uint)_map.Cells.Length)
            {
                continue;
            }

            _map.Cells[index] = _after[i];
        }
    }
}
