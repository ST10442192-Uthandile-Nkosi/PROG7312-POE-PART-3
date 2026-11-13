namespace SmartX.Shared.Collections;

/// <summary>
/// Height-balanced binary search tree. Telemetry usually arrives in time order, which makes a
/// plain BST a linked list and turns a range query into a full scan. AVL rotations keep the
/// height near 1.44 log2(n), so a timestamp window stays O(log n + matches) even for a long history.
/// Not thread-safe; callers that share an instance must provide their own lock.
/// </summary>
public sealed class AvlTree<TKey, TValue> where TKey : IComparable<TKey>
{
    private Node? _root;
    private int _count;

    public int Count => _count;

    public int Height => HeightOf(_root);

    public void Insert(TKey key, TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        _root = Insert(_root, key, value);
    }

    public bool Remove(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var before = _count;
        _root = Delete(_root, key, adjustCount: true);
        return _count != before;
    }

    public bool TryGetRoot(out TKey key, out TValue value)
    {
        if (_root is null)
        {
            key = default!;
            value = default!;
            return false;
        }

        key = _root.Key;
        value = _root.Value;
        return true;
    }

    public IReadOnlyList<TKey> KeysInOrder()
    {
        var keys = new List<TKey>(_count);
        CollectKeys(_root, keys);
        return keys;
    }

    public IReadOnlyList<TValue> InOrder()
    {
        var values = new List<TValue>(_count);
        CollectValues(_root, values);
        return values;
    }

    /// <summary>
    /// Values whose keys are between <paramref name="low"/> and <paramref name="high"/>, inclusive, in sort order.
    /// An inverted range or an empty tree yields an empty list.
    /// </summary>
    public IReadOnlyList<TValue> RangeInclusive(TKey low, TKey high)
    {
        ArgumentNullException.ThrowIfNull(low);
        ArgumentNullException.ThrowIfNull(high);
        if (low.CompareTo(high) > 0 || _root is null)
        {
            return [];
        }

        var matches = new List<TValue>();
        CollectRange(_root, low, high, matches);
        return matches;
    }

    public bool CheckInvariants()
    {
        TKey previous = default!;
        var hasPrevious = false;
        var visited = 0;
        var ok = Verify(_root, ref previous, ref hasPrevious, ref visited);
        return ok && visited == _count;
    }

    private Node Insert(Node? node, TKey key, TValue value)
    {
        if (node is null)
        {
            _count++;
            return new Node(key, value);
        }

        var comparison = key.CompareTo(node.Key);
        if (comparison < 0)
        {
            node.Left = Insert(node.Left, key, value);
        }
        else if (comparison > 0)
        {
            node.Right = Insert(node.Right, key, value);
        }
        else
        {
            node.Value = value;
        }

        return Rebalance(node);
    }

    private Node? Delete(Node? node, TKey key, bool adjustCount)
    {
        if (node is null)
        {
            return null;
        }

        var comparison = key.CompareTo(node.Key);
        if (comparison < 0)
        {
            node.Left = Delete(node.Left, key, adjustCount);
        }
        else if (comparison > 0)
        {
            node.Right = Delete(node.Right, key, adjustCount);
        }
        else
        {
            if (adjustCount)
            {
                _count--;
            }

            if (node.Left is null)
            {
                return node.Right;
            }

            if (node.Right is null)
            {
                return node.Left;
            }

            var successor = Min(node.Right);
            node.Key = successor.Key;
            node.Value = successor.Value;
            node.Right = Delete(node.Right, successor.Key, adjustCount: false);
        }

        return Rebalance(node);
    }

    private static Node Rebalance(Node node)
    {
        Update(node);
        var balance = HeightOf(node.Left) - HeightOf(node.Right);
        if (balance > 1)
        {
            // Left child is right-heavy: left-right case, otherwise a single right rotation.
            if (HeightOf(node.Left!.Left) < HeightOf(node.Left.Right))
            {
                node.Left = RotateLeft(node.Left);
            }

            return RotateRight(node);
        }

        if (balance < -1)
        {
            // Right child is left-heavy: right-left case, otherwise a single left rotation.
            if (HeightOf(node.Right!.Right) < HeightOf(node.Right.Left))
            {
                node.Right = RotateRight(node.Right);
            }

            return RotateLeft(node);
        }

        return node;
    }

    private static Node RotateLeft(Node pivot)
    {
        var raised = pivot.Right!;
        pivot.Right = raised.Left;
        raised.Left = pivot;
        Update(pivot);
        Update(raised);
        return raised;
    }

    private static Node RotateRight(Node pivot)
    {
        var raised = pivot.Left!;
        pivot.Left = raised.Right;
        raised.Right = pivot;
        Update(pivot);
        Update(raised);
        return raised;
    }

    private static void Update(Node node) =>
        node.Height = 1 + Math.Max(HeightOf(node.Left), HeightOf(node.Right));

    private static int HeightOf(Node? node) => node?.Height ?? 0;

    private static Node Min(Node node)
    {
        while (node.Left is not null)
        {
            node = node.Left;
        }

        return node;
    }

    private static void CollectKeys(Node? node, List<TKey> keys)
    {
        if (node is null)
        {
            return;
        }

        CollectKeys(node.Left, keys);
        keys.Add(node.Key);
        CollectKeys(node.Right, keys);
    }

    private static void CollectValues(Node? node, List<TValue> values)
    {
        if (node is null)
        {
            return;
        }

        CollectValues(node.Left, values);
        values.Add(node.Value);
        CollectValues(node.Right, values);
    }

    private static void CollectRange(Node? node, TKey low, TKey high, List<TValue> matches)
    {
        if (node is null)
        {
            return;
        }

        if (node.Key.CompareTo(low) > 0)
        {
            CollectRange(node.Left, low, high, matches);
        }

        if (node.Key.CompareTo(low) >= 0 && node.Key.CompareTo(high) <= 0)
        {
            matches.Add(node.Value);
        }

        if (node.Key.CompareTo(high) < 0)
        {
            CollectRange(node.Right, low, high, matches);
        }
    }

    private static bool Verify(Node? node, ref TKey previous, ref bool hasPrevious, ref int visited)
    {
        if (node is null)
        {
            return true;
        }

        if (!Verify(node.Left, ref previous, ref hasPrevious, ref visited))
        {
            return false;
        }

        if (hasPrevious && previous.CompareTo(node.Key) >= 0)
        {
            return false;
        }

        var leftHeight = HeightOf(node.Left);
        var rightHeight = HeightOf(node.Right);
        if (node.Height != 1 + Math.Max(leftHeight, rightHeight) || Math.Abs(leftHeight - rightHeight) > 1)
        {
            return false;
        }

        previous = node.Key;
        hasPrevious = true;
        visited++;
        return Verify(node.Right, ref previous, ref hasPrevious, ref visited);
    }

    private sealed class Node
    {
        public Node(TKey key, TValue value)
        {
            Key = key;
            Value = value;
            Height = 1;
        }

        public TKey Key { get; set; }
        public TValue Value { get; set; }
        public int Height { get; set; }
        public Node? Left { get; set; }
        public Node? Right { get; set; }
    }
}
