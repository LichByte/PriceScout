using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace PriceScout;

public sealed record Ingredient(uint ItemId, string Name, int Amount);

public sealed record RecipeInfo(uint RecipeId, string Job, int Level, int AmountResult, IReadOnlyList<Ingredient> Ingredients);

// Recipes indexed by the item they produce, built from the game's Recipe sheet.
public static class Recipes
{
    private static Dictionary<uint, List<RecipeInfo>>? byResult;

    public static IReadOnlyList<RecipeInfo> ForItem(uint itemId)
    {
        byResult ??= Build();
        return byResult.TryGetValue(itemId, out var recipes) ? recipes : new List<RecipeInfo>();
    }

    private static Dictionary<uint, List<RecipeInfo>> Build()
    {
        var result = new Dictionary<uint, List<RecipeInfo>>();
        foreach (var recipe in Plugin.DataManager.GetExcelSheet<Recipe>())
        {
            var resultId = recipe.ItemResult.RowId;
            if (resultId == 0)
                continue;

            var ingredients = new List<Ingredient>();
            for (var i = 0; i < recipe.Ingredient.Count; i++)
            {
                var ingredient = recipe.Ingredient[i];
                var amount = recipe.AmountIngredient[i];
                if (ingredient.RowId == 0 || amount == 0 || !ingredient.IsValid)
                    continue;

                ingredients.Add(new Ingredient(ingredient.RowId, ingredient.Value.Name.ExtractText(), amount));
            }

            if (ingredients.Count == 0)
                continue;

            var job = recipe.CraftType.IsValid ? recipe.CraftType.Value.Name.ExtractText() : "?";
            var level = recipe.RecipeLevelTable.IsValid ? recipe.RecipeLevelTable.Value.ClassJobLevel : 0;
            var info = new RecipeInfo(recipe.RowId, job, level, recipe.AmountResult, ingredients);

            if (!result.TryGetValue(resultId, out var list))
                result[resultId] = list = new List<RecipeInfo>();
            list.Add(info);
        }

        return result;
    }
}
