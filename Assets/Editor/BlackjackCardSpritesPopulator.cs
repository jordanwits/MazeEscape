using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor tool that creates / refreshes the <see cref="BlackjackCardSprites"/> asset by loading the Bridge
/// playing-card sprites by file name. Run from Tools ▸ Blackjack ▸ Populate Card Sprites.
/// </summary>
public static class BlackjackCardSpritesPopulator
{
    const string SpriteFolder = "Assets/Bridge Playing Cards/Sprites";
    const string AssetPath = "Assets/Prefabs/Maze Components/Carnival/BlackjackCardSprites.asset";

    [MenuItem("Tools/Blackjack/Populate Card Sprites")]
    public static void Populate()
    {
        BlackjackCardSprites so = AssetDatabase.LoadAssetAtPath<BlackjackCardSprites>(AssetPath);
        if (so == null)
        {
            so = ScriptableObject.CreateInstance<BlackjackCardSprites>();
            AssetDatabase.CreateAsset(so, AssetPath);
        }

        so.cards = new Sprite[BlackjackCard.DeckSize];
        int missing = 0;
        for (byte c = 0; c < BlackjackCard.DeckSize; c++)
        {
            string spriteName = BlackjackCard.SpriteName(c);
            Sprite sp = AssetDatabase.LoadAssetAtPath<Sprite>($"{SpriteFolder}/{spriteName}.png");
            if (sp == null)
            {
                Debug.LogWarning($"[BlackjackCardSpritesPopulator] Missing sprite for {spriteName}");
                missing++;
            }
            so.cards[c] = sp;
        }

        so.back = AssetDatabase.LoadAssetAtPath<Sprite>($"{SpriteFolder}/Back-R.png"); // red back
        so.empty = null; // Bridge pack has no "empty" card; the table view doesn't reference it.

        EditorUtility.SetDirty(so);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[BlackjackCardSpritesPopulator] Populated {AssetPath} — {BlackjackCard.DeckSize - missing}/{BlackjackCard.DeckSize} face cards, back={(so.back != null)}, empty={(so.empty != null)}.");
    }
}
