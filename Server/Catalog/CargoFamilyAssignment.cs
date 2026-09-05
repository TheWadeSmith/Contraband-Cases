using System.Collections.ObjectModel;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;

namespace ContrabandCases.Server.Catalog;

public sealed class CargoFamilyAssignment
{
    internal CargoFamilyAssignment(IEnumerable<FamilyId> families)
    {
        var snapshot = families.ToArray();
        if (snapshot.Length != 3 ||
            snapshot.Distinct().Count() != snapshot.Length)
        {
            throw new CargoCatalogValidationException(
                "A manifest assignment requires exactly three distinct families.");
        }

        Families = new ReadOnlyCollection<FamilyId>(snapshot);
    }

    public IReadOnlyList<FamilyId> Families { get; }
}

public static class CargoFamilyAssignmentEnumerator
{
    public static IReadOnlyList<CargoFamilyAssignment> Enumerate(
        IEnumerable<ResolvedCargoLot> lots,
        string caseTemplateId = ModConstants.CaseTemplateId)
    {
        ArgumentNullException.ThrowIfNull(lots);
        var families = lots
            .Select(lot => CaseContracts.SelectionFamily(caseTemplateId, lot?.Identity ??
                throw new CargoCatalogValidationException(
                    "A catalog cannot contain a null cargo lot.")))
            .Distinct()
            .OrderBy(family => family)
            .ToArray();
        var assignments = new List<CargoFamilyAssignment>();
        for (var first = 0; first < families.Length; first++)
        {
            for (var second = 0; second < families.Length; second++)
            {
                if (second == first)
                {
                    continue;
                }
                for (var third = 0; third < families.Length; third++)
                {
                    if (third == first || third == second)
                    {
                        continue;
                    }

                    assignments.Add(new CargoFamilyAssignment(
                        [families[first], families[second], families[third]]));
                }
            }
        }

        return new ReadOnlyCollection<CargoFamilyAssignment>(assignments);
    }
}
