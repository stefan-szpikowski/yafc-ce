using System.Collections.Generic;
using Xunit;

namespace Yafc.Model.Tests.Model;

public class SolverModelTests {
    // The models in these tests do not need real recipes or links: they are only used by the infeasibility diagnostics, which are not triggered here.
    private static SolverModel CreateModel(int columns, int rows)
        => new([.. new ISolverRow[columns]], [.. new IProductionLink[rows]]);

    [Fact]
    public void Solve_AddsCoefficientsForTheSameRowAndColumnTogether() {
        SolverModel model = CreateModel(1, 1);
        model.columnNames[0] = "recipe";
        model.columnCosts[0] = 1;
        model.rowNames[0] = "link";
        model.rowMin[0] = 3;
        model.rowMax[0] = 3;
        model.coefficients.Add(new SolverModel.Coefficient(0, 0, 1f));
        model.coefficients.Add(new SolverModel.Coefficient(0, 0, 0.5f));

        SolverModel.Solution solution = model.Solve();

        Assert.True(solution.hasSolution);
        Assert.False(solution.diagnosticsRan);
        Assert.InRange(Assert.Single(solution.recipesPerSecond), 2 - 1e-6, 2 + 1e-6);
        _ = Assert.Single(solution.rowBasisStatus);
    }

    [Fact]
    public void Solve_UsesFixedRecipesPerSecond() {
        SolverModel model = CreateModel(2, 1);
        model.columnNames[0] = "fixed producer";
        model.columnNames[1] = "consumer";
        model.columnCosts[1] = 1;
        model.fixedRecipesPerSecond[0] = 0.5;
        model.rowNames[0] = "link";
        model.coefficients.Add(new SolverModel.Coefficient(0, 0, 2f));
        model.coefficients.Add(new SolverModel.Coefficient(0, 1, -1f));

        SolverModel.Solution solution = model.Solve();

        Assert.True(solution.hasSolution);
        Assert.Equal(new double[] { 0.5, 1 }, solution.recipesPerSecond, new DoubleComparer());
    }

    [Fact]
    public void Solve_IgnoresRowsWithInfiniteBounds() {
        SolverModel model = CreateModel(2, 1);
        model.columnNames[0] = "fixed producer";
        model.columnNames[1] = "consumer";
        model.columnCosts[1] = 1;
        model.fixedRecipesPerSecond[0] = 0.5;
        model.rowNames[0] = "link";
        model.rowMin[0] = double.NegativeInfinity;
        model.rowMax[0] = double.PositiveInfinity;
        model.coefficients.Add(new SolverModel.Coefficient(0, 0, 2f));
        model.coefficients.Add(new SolverModel.Coefficient(0, 1, -1f));

        SolverModel.Solution solution = model.Solve();

        Assert.True(solution.hasSolution);
        Assert.Equal(new double[] { 0.5, 0 }, solution.recipesPerSecond, new DoubleComparer());
    }

    private sealed class DoubleComparer : IEqualityComparer<double> {
        public bool Equals(double x, double y) => global::System.Math.Abs(x - y) < 1e-6;
        public int GetHashCode(double obj) => 0;
    }
}
