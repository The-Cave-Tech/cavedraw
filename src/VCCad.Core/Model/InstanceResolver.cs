namespace VCCad.Core.Model;

/// <summary>
/// What a re-resolution did: how many instances were rebuilt from their definition, and every link that could
/// not be followed.
/// </summary>
/// <param name="Refreshed">The number of instances whose content was replaced with the current definition.</param>
/// <param name="NotFollowed">
/// One line per link that was refused, naming the id and the reason - a definition the document does not have,
/// or a reference that leads back to itself. Reported rather than thrown, because an unresolved instance is not a
/// broken document: it still holds the copy it was last resolved with, which is what it draws.
/// </param>
public sealed record InstanceResolution(int Refreshed, IReadOnlyList<string> NotFollowed);

/// <summary>
/// The step that **follows** an instance's link: it rebuilds every instance from the definition it names.
///
/// This is the half `use` was missing. The reader records the id an instance came from in
/// <see cref="ArtGroup.SourceId"/> and keeps a copy of the definition's content, so the picture is right in a
/// single render and a round trip keeps the link - and none of that is the same as *honouring* the link. Editing
/// the definition has to reach every instance of it, or "an instance is a reference, not a copy" is a promise the
/// model records and never keeps.
///
/// **When this runs, and why there.** Not on every property change - an edit arrives through many commands and
/// through direct mutation, and rebuilding instance trees mid-drag would multiply undo entries and make a cycle an
/// edit-time recursion instead of a refusal. Not on read alone - that is what already worked, and it does not
/// reach an instance of a definition edited after the file was opened. It runs as **one explicit, undoable step**
/// (<see cref="Commands.RefreshInstancesCommand"/>), which is the same discipline a width profile is edited under:
/// the reference is never changed on its own, and the definition and the instances that follow it move together.
///
/// **Cycles cannot be reintroduced.** Resolution remembers the ids it is inside, and a second attempt at one is
/// refused by name rather than followed - the same rule the reader applies to a circular `use`, stated once here
/// so that an edit path cannot create a loop the reader would refuse. An edit can add an instance to a definition
/// (that is how a definition comes to contain one), and if it names an id already being resolved the resolver
/// stops and says so instead of recursing.
///
/// **The instance's own transform is never touched.** A `use` places its instance by `x`, `y` and `transform`,
/// and a `symbol` is sized by the use that draws it, so all of that is the *instance's* placement and stays on the
/// instance group. Only the children are replaced, which is why re-resolving an instance inside a transformed
/// group does not apply that group's transform a second time: the frame is exactly what
/// <see cref="Selection.SelectionEngine.ToArtboard"/> already composes, with the instance's placement as one of
/// its terms.
/// </summary>
public static class InstanceResolver
{
    /// <summary>Rebuilds every instance in the document from the definition it names.</summary>
    public static InstanceResolution Resolve(CadDocument document)
    {
        var resolving = new HashSet<string>(StringComparer.Ordinal);
        var notFollowed = new List<string>();
        int refreshed = 0;

        // The tree is mutated as instances are rebuilt, so the walk is over a snapshot, and only the outermost
        // instances are taken: one that an instance contains is resolved by its own parent's recursion, which is
        // also what carries the cycle set down a chain. A nested instance left in this list would be rebuilt twice,
        // and once its parent had replaced it, the second rebuild would be of a detached copy.
        foreach (ArtGroup instance in document.AllGroups()
            .Where(group => group.SourceId is { Length: > 0 } && !HasInstanceAncestor(group))
            .ToArray())
        {
            if (Materialise(instance, document, resolving, notFollowed))
            {
                refreshed++;
            }
        }

        return new InstanceResolution(refreshed, notFollowed);
    }

    /// <summary>
    /// Whether this group is an instance - a group that names a definition rather than being a group in its own
    /// right. The reader's own marker for `use`, which is what the writer records as `data-source`.
    /// </summary>
    public static bool IsInstance(LayerItem item) => item is ArtGroup { SourceId.Length: > 0 };

    /// <summary>Whether this item sits inside another instance, which is what resolves it.</summary>
    private static bool HasInstanceAncestor(LayerItem item)
    {
        for (IItemContainer? container = item.Container;
             container is not null;
             container = (container as LayerItem)?.Container)
        {
            if (container is LayerItem ancestor && IsInstance(ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces one instance's children with a copy of its definition, and resolves the instances inside that
    /// copy - a definition may itself use another definition, and the chain has to be followed the whole way down.
    /// </summary>
    private static bool Materialise(
        ArtGroup instance, CadDocument document, HashSet<string> resolving, List<string> notFollowed)
    {
        string id = instance.SourceId!;

        if (document.FindDefinition(id) is not { } definition)
        {
            // A definition the document does not have. The instance keeps the copy it already holds - blanking it
            // would turn a lost asset into artwork that vanished - and the dangling name is reported so the loss
            // is visible. CadDocument.MissingDefinitions answers the same question document-wide.
            notFollowed.Add($"{id} (no definition)");
            return false;
        }

        if (!resolving.Add(id))
        {
            // The cycle. Refused by name: the id being resolved is remembered for as long as it is being resolved,
            // so a reference back to it is the loop itself, and naming it is more use than a depth cap.
            notFollowed.Add($"{id} (circular)");
            return false;
        }

        try
        {
            foreach (LayerItem child in instance.Children.ToArray())
            {
                instance.RemoveItem(child);
            }

            foreach (LayerItem content in definition.Children)
            {
                instance.AddItem(content.Clone());
            }

            foreach (LayerItem child in instance.Children)
            {
                foreach (ArtGroup nested in NearestInstances(child))
                {
                    Materialise(nested, document, resolving, notFollowed);
                }
            }
        }
        finally
        {
            resolving.Remove(id);
        }

        return true;
    }

    /// <summary>
    /// The instances inside a piece of a definition's content whose nearest instance ancestor is the instance
    /// being resolved - each one is followed here, and the ones it contains are followed by its own recursion.
    /// </summary>
    private static IEnumerable<ArtGroup> NearestInstances(LayerItem item)
    {
        if (item is ArtGroup instance && IsInstance(instance))
        {
            yield return instance;
            yield break;
        }

        if (item is ArtGroup group)
        {
            foreach (LayerItem child in group.Children)
            {
                foreach (ArtGroup nested in NearestInstances(child))
                {
                    yield return nested;
                }
            }
        }
    }
}
