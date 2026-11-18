using System;
using System.Collections.Generic;
using Google.OrTools.LinearSolver;
using Serilog;
using Yafc.Core;

namespace Yafc.Model;

/// <summary>
/// A snapshot of the linear program that <see cref="ProductionTable.Solve"/> asks the solver to solve. It is built from the model on the model
/// thread (see <c>ProductionTable.BuildSolverModel</c>), and afterwards <see cref="Solve"/> only needs the data stored here, except for the
/// infeasibility diagnostics that look at the <see cref="recipes"/> and <see cref="links"/> that were used to build it.
/// </summary>
/// <remarks>
/// The linear program has one column (variable) for each <see cref="ISolverRow"/>, which is the number of recipe runs per second,
/// and one row (constraint) for each <see cref="IProductionLink"/>, which constrains the production minus the consumption of the linked goods.
/// The columns and rows are numbered in the same order as <see cref="recipes"/> and <see cref="links"/>.
/// </remarks>
internal sealed class SolverModel {
    private static readonly ILogger logger = Logging.GetLogger<SolverModel>();

    /// <summary>
    /// One non-zero entry of the constraint matrix: one run of the recipe in <see cref="column"/> produces <paramref name="amount"/> (consumes,
    /// if negative) of the goods of the link in <see cref="row"/>.
    /// </summary>
    internal readonly record struct Coefficient(int row, int column, float amount);

    /// <summary>
    /// The result of <see cref="Solve"/>.
    /// </summary>
    internal sealed class Solution(Solver.ResultStatus status, bool diagnosticsRan) {
        /// <summary>
        /// The status reported by the last solver run. If <see cref="diagnosticsRan"/> is <see langword="true"/>, this is the status of the
        /// run that included the slack variables.
        /// </summary>
        public Solver.ResultStatus status { get; } = status;
        /// <summary>
        /// <see langword="true"/> if the model without slack variables had no solution, and the model was solved again with the slack variables.
        /// </summary>
        public bool diagnosticsRan { get; } = diagnosticsRan;
        /// <summary>
        /// <see langword="true"/> if <see cref="status"/> is a status that provides values for the other properties.
        /// </summary>
        public bool hasSolution => status is Solver.ResultStatus.OPTIMAL or Solver.ResultStatus.FEASIBLE;
        /// <summary>
        /// The value of each column. Only valid if <see cref="hasSolution"/> is <see langword="true"/>.
        /// </summary>
        public double[] recipesPerSecond { get; init; } = [];
        /// <summary>
        /// The basis status of each row. Only valid if <see cref="hasSolution"/> is <see langword="true"/>.
        /// </summary>
        public Solver.BasisStatus[] rowBasisStatus { get; init; } = [];
        /// <summary>
        /// For each row, the value of the slack variable that lets the row have unneeded production, or <see langword="null"/> if the row has no
        /// such slack variable or the variable is at its lower bound. Only valid if <see cref="diagnosticsRan"/> and <see cref="hasSolution"/>.
        /// </summary>
        public double?[] positiveSlack { get; init; } = [];
        /// <summary>
        /// For each row, the value of the slack variable that lets the row have unmet consumption, or <see langword="null"/> if the row has no
        /// such slack variable or the variable is at its lower bound. Only valid if <see cref="diagnosticsRan"/> and <see cref="hasSolution"/>.
        /// </summary>
        public double?[] negativeSlack { get; init; } = [];
    }

    /// <summary>
    /// The recipe rows that correspond to the columns.
    /// </summary>
    public List<ISolverRow> recipes { get; }
    /// <summary>
    /// The links that correspond to the rows.
    /// </summary>
    public List<IProductionLink> links { get; }

    /// <summary>
    /// The name of each column, for the solver's logs.
    /// </summary>
    public string[] columnNames { get; }
    /// <summary>
    /// The objective coefficient (cost per recipe run per second) of each column.
    /// </summary>
    public double[] columnCosts { get; }
    /// <summary>
    /// If not <see langword="null"/>, the column is fixed to this number of recipe runs per second.
    /// </summary>
    public double?[] fixedRecipesPerSecond { get; }
    /// <summary>
    /// The name of each row, for the solver's logs.
    /// </summary>
    public string[] rowNames { get; }
    /// <summary>
    /// The lower bound of each row. Rows that should not constrain the solution have infinite bounds.
    /// </summary>
    public double[] rowMin { get; }
    /// <summary>
    /// The upper bound of each row. Rows that should not constrain the solution have infinite bounds.
    /// </summary>
    public double[] rowMax { get; }
    /// <summary>
    /// The non-zero entries of the constraint matrix, in the order they were added. Entries for the same row and column are added together.
    /// </summary>
    public List<Coefficient> coefficients { get; } = [];

    public SolverModel(List<ISolverRow> recipes, List<IProductionLink> links) {
        this.recipes = recipes;
        this.links = links;
        columnNames = new string[recipes.Count];
        columnCosts = new double[recipes.Count];
        fixedRecipesPerSecond = new double?[recipes.Count];
        rowNames = new string[links.Count];
        rowMin = new double[links.Count];
        rowMax = new double[links.Count];
    }

    /// <summary>
    /// Solves this model. If it has no solution, this reports the likely cause by solving it again, with slack variables added to the rows that
    /// could be the reason (see <see cref="ProductionTable.GetInfeasibilityCandidates"/>).
    /// </summary>
    public Solution Solve() {
        using var solver = DataUtils.CreateSolver();
        var objective = solver.Objective();
        objective.SetMinimization();
        Variable[] vars = new Variable[recipes.Count];

        for (int i = 0; i < vars.Length; i++) {
            var variable = solver.MakeNumVar(0f, double.PositiveInfinity, columnNames[i]);

            if (fixedRecipesPerSecond[i] is double fixedRps) {
                variable.SetBounds(fixedRps, fixedRps);
            }

            vars[i] = variable;
        }

        Constraint[] constraints = new Constraint[links.Count];

        for (int i = 0; i < constraints.Length; i++) {
            constraints[i] = solver.MakeConstraint(rowMin[i], rowMax[i], rowNames[i]);
        }

        foreach (var (row, column, amount) in coefficients) {
            // GetCoefficient will return 0 when the variable is not available in the constraint
            float total = amount + (float)constraints[row].GetCoefficient(vars[column]);
            constraints[row].SetCoefficient(vars[column], total);
        }

        for (int i = 0; i < vars.Length; i++) {
            objective.SetCoefficient(vars[i], columnCosts[i]);
        }

        var result = solver.Solve();
        bool diagnosticsRan = false;
        (Variable? positive, Variable? negative)[] slackVars = new (Variable? positive, Variable? negative)[links.Count];

        if (result is not Solver.ResultStatus.FEASIBLE and not Solver.ResultStatus.OPTIMAL) {
            diagnosticsRan = true;
            objective.Clear();
            var (deadlocks, splits) = ProductionTable.GetInfeasibilityCandidates(recipes);

            // Solution does not exist. Adding slack variables to find the reason
            foreach (var link in deadlocks) {
                // Adding negative slack to possible deadlocks (loops)
                var constraint = constraints[link.solverIndex];
                float cost = MathF.Abs(link.goods.target.Cost());
                var negativeSlack = solver.MakeNumVar(0d, double.PositiveInfinity, "negative-slack." + link.goods.QualityName());
                constraint.SetCoefficient(negativeSlack, cost);
                objective.SetCoefficient(negativeSlack, 1f);
                slackVars[link.solverIndex].negative = negativeSlack;
            }

            foreach (var link in splits) {
                // Adding positive slack to splits
                float cost = MathF.Abs(link.goods.target.Cost());
                var constraint = constraints[link.solverIndex];
                var positiveSlack = solver.MakeNumVar(0d, double.PositiveInfinity, "positive-slack." + link.goods.QualityName());
                constraint.SetCoefficient(positiveSlack, -cost);
                objective.SetCoefficient(positiveSlack, 1f);
                slackVars[link.solverIndex].positive = positiveSlack;
            }

            result = solver.Solve();

            logger.Information("Solver finished with result {result}", result);
        }

        if (result is not Solver.ResultStatus.FEASIBLE and not Solver.ResultStatus.OPTIMAL) {
            return new Solution(result, diagnosticsRan);
        }

        double?[] positiveSlackValues = new double?[links.Count];
        double?[] negativeSlackValues = new double?[links.Count];

        if (diagnosticsRan) {
            for (int i = 0; i < slackVars.Length; i++) {
                var (posSlack, negSlack) = slackVars[i];

                if (posSlack is not null && posSlack.BasisStatus() != Solver.BasisStatus.AT_LOWER_BOUND) {
                    positiveSlackValues[i] = posSlack.SolutionValue();
                }

                if (negSlack is not null && negSlack.BasisStatus() != Solver.BasisStatus.AT_LOWER_BOUND) {
                    negativeSlackValues[i] = negSlack.SolutionValue();
                }
            }
        }

        double[] recipesPerSecond = new double[vars.Length];

        for (int i = 0; i < vars.Length; i++) {
            recipesPerSecond[i] = vars[i].SolutionValue();
        }

        Solver.BasisStatus[] rowBasisStatus = new Solver.BasisStatus[constraints.Length];

        for (int i = 0; i < constraints.Length; i++) {
            rowBasisStatus[i] = constraints[i].BasisStatus();
        }

        return new Solution(result, diagnosticsRan) {
            recipesPerSecond = recipesPerSecond,
            rowBasisStatus = rowBasisStatus,
            positiveSlack = positiveSlackValues,
            negativeSlack = negativeSlackValues,
        };
    }
}
