using System.Collections.Generic;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// Cat or dog for each player. The player's body is built from two parts, a head ("head/head") and a body
/// ("cylinder"), and the game's dog NPC uses the same two parts with different meshes (the dog head has
/// its own ears) and its own texture. Being a dog means putting the dog NPC's head and body meshes on the
/// player; being a cat puts the player's original meshes back.
/// </summary>
internal static class PlayerSpecies
{
    internal static Mesh? DogHead { get; private set; }
    internal static Mesh? DogBody { get; private set; }
    internal static Texture? DogTexture { get; private set; }
    internal static bool DogReady => DogHead != null && DogBody != null && DogTexture != null;

    private static float _nextScan;
    private static bool _warned;

    // The player's own meshes, remembered the first time they are swapped so a cat can be restored
    private static readonly Dictionary<MeshFilter, Mesh> OriginalMeshes = new();

    internal static bool IsDog(int playerIndex)
    {
        string value = (playerIndex == 2 ? Plugin.SpeciesP2 : Plugin.SpeciesP1)?.Value ?? "Cat";
        return string.Equals(value, "Dog", System.StringComparison.OrdinalIgnoreCase);
    }

    internal static void Set(int playerIndex, bool dog)
    {
        var entry = playerIndex == 2 ? Plugin.SpeciesP2 : Plugin.SpeciesP1;
        if (entry == null) return;
        entry.Value = dog ? "Dog" : "Cat";
        PlayerCustomization.ApplyToAllActivePlayers();
    }

    /// <summary>Finds the dog NPC's head, body and texture in the loaded scene (cached once found).</summary>
    internal static bool EnsureDogAssets()
    {
        if (DogReady) return true;
        if (Time.unscaledTime < _nextScan) return false;
        _nextScan = Time.unscaledTime + 3f;

        var roots = new List<Component>();
        roots.AddRange(UnityEngine.Object.FindObjectsByType<BasicDogGuy>(FindObjectsInactive.Include, FindObjectsSortMode.None));
        roots.AddRange(UnityEngine.Object.FindObjectsByType<DogGuy>(FindObjectsInactive.Include, FindObjectsSortMode.None));

        foreach (var root in roots)
        {
            if (root == null) continue;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var parent = filter.transform.parent;
                if (parent == null || filter.sharedMesh == null) continue;

                if (filter.name == "Head" && parent.name == "Head")
                {
                    DogHead = filter.sharedMesh;
                    var rend = filter.GetComponent<MeshRenderer>();
                    if (rend != null && rend.sharedMaterial != null)
                        DogTexture = rend.sharedMaterial.GetTexture("_BaseMap");
                }
                else if (filter.name == "cylinder" && parent.name == "Torso")
                {
                    DogBody = filter.sharedMesh;
                }
            }
            if (DogHead != null && DogBody != null && DogTexture != null)
            {
                Plugin.Log.LogInfo($"[PlayerSpecies] Dog model found on '{root.name}' (head '{DogHead.name}', body '{DogBody.name}').");
                return true;
            }
        }

        if (!_warned)
        {
            _warned = true;
            Plugin.Log.LogWarning("[PlayerSpecies] No dog NPC in this scene yet, so the dog model is not available (will keep looking).");
        }
        return false;
    }

    /// <summary>Puts the dog meshes on a player (dog = true) or the original cat meshes back.</summary>
    internal static void Apply(sCosmeticAppearance appearance, bool dog)
    {
        var head = appearance.transform.Find("head")?.Find("head")?.GetComponent<MeshFilter>();
        var body = appearance.transform.Find("cylinder")?.GetComponent<MeshFilter>();

        Swap(head, dog ? DogHead : null);
        Swap(body, dog ? DogBody : null);
    }

    private static void Swap(MeshFilter? filter, Mesh? dogMesh)
    {
        if (filter == null) return;

        if (dogMesh != null)
        {
            if (!OriginalMeshes.ContainsKey(filter) && filter.sharedMesh != null)
                OriginalMeshes[filter] = filter.sharedMesh;
            if (filter.sharedMesh != dogMesh) filter.sharedMesh = dogMesh;
        }
        else if (OriginalMeshes.TryGetValue(filter, out var original) && original != null)
        {
            if (filter.sharedMesh != original) filter.sharedMesh = original;
        }
    }
}
