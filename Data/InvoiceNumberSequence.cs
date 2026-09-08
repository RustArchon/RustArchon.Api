// Copyright ©2026 Scott Blomfield

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;

namespace RustArchon.Api.Data;

/// <summary>
/// The counter invoice numbers are drawn from, one row per numbering scope.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A table rather than a Postgres sequence, and that is the whole point.</strong> Some
/// jurisdictions require invoice numbers to be gapless, and <c>nextval</c> is explicitly not
/// transactional - a sequence hands out a value that is consumed even if the transaction that took it
/// rolls back. That is a feature for surrogate keys and a defect here: a finalisation that fails would
/// silently burn a number, and a missing number is exactly what a gapless requirement exists to
/// prevent. A counter row taken under <c>SELECT ... FOR UPDATE</c> inside the finalising transaction
/// rolls back with it.
/// </para>
/// <para>
/// The cost is that invoice finalisation serialises on this row. At any volume this system will ever
/// see that is free, and it buys a guarantee that is very hard to retrofit once real invoices exist.
/// </para>
/// <para>
/// <see cref="Scope"/> exists so "per legal entity per year" - which some jurisdictions want - is a
/// data change rather than a schema change later. Today there is one scope,
/// <see cref="DefaultScope"/>.
/// </para>
/// </remarks>
[Table("InvoiceNumberSequence")]
public class InvoiceNumberSequence : Entity
{
    /// <summary>The single scope in use today - one global, ever-increasing series.</summary>
    public const string DefaultScope = "default";

    /// <summary>What this counter numbers. See this class's remarks.</summary>
    [Required]
    [MaxLength(60)]
    public string Scope { get; set; } = DefaultScope;

    /// <summary>The number the next invoice finalised in this scope will take.</summary>
    public long NextValue { get; set; } = 1;

    /// <summary>
    /// Optional text put in front of the number when it is rendered - "INV-" and the like. Stored so
    /// changing it is a settings change rather than a deployment.
    /// </summary>
    [MaxLength(20)]
    public string Prefix { get; set; } = "INV-";

    /// <summary>
    /// How many digits the number is padded to, so numbers sort as text and line up on a page.
    /// </summary>
    public int PadWidth { get; set; } = 6;

    /// <summary>Renders <paramref name="value"/> the way it appears on an invoice.</summary>
    public string Format(long value) => Prefix + value.ToString().PadLeft(PadWidth, '0');
}
