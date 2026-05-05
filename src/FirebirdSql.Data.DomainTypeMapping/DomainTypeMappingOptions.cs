/*
 *    The contents of this file are subject to the Initial
 *    Developer's Public License Version 1.0 (the "License");
 *    you may not use this file except in compliance with the
 *    License. You may obtain a copy of the License at
 *    https://github.com/FirebirdSQL/NETProvider/raw/master/license.txt.
 *
 *    Software distributed under the License is distributed on
 *    an "AS IS" basis, WITHOUT WARRANTY OF ANY KIND, either
 *    express or implied. See the License for the specific
 *    language governing rights and limitations under the License.
 *
 *    All Rights Reserved.
 */

//$Authors = Ebubekir Cagri Sen (ebubekircagrisen@gmail.com)

using System;

namespace FirebirdSql.Data.DomainTypeMapping;

public sealed class DomainTypeMappingOptions
{
	public string BooleanDomains { get; set; }
	public string GuidDomains { get; set; }

	public DomainTypeMappingOptions Clone()
	{
		return new DomainTypeMappingOptions
		{
			BooleanDomains = BooleanDomains,
			GuidDomains = GuidDomains,
		};
	}

	internal CompiledDomainOptions Compile()
	{
		return new CompiledDomainOptions(
			DomainPatternList.Parse(BooleanDomains),
			DomainPatternList.Parse(GuidDomains));
	}
}

internal sealed class CompiledDomainOptions
{
	public DomainPatternList BooleanDomains { get; }
	public DomainPatternList GuidDomains { get; }
	public bool HasAny => BooleanDomains.HasAny || GuidDomains.HasAny;

	public CompiledDomainOptions(DomainPatternList booleanDomains, DomainPatternList guidDomains)
	{
		BooleanDomains = booleanDomains ?? throw new ArgumentNullException(nameof(booleanDomains));
		GuidDomains = guidDomains ?? throw new ArgumentNullException(nameof(guidDomains));
	}

	public DomainKind Classify(string domainName)
	{
		if (BooleanDomains.Matches(domainName))
			return DomainKind.Boolean;
		if (GuidDomains.Matches(domainName))
			return DomainKind.Guid;
		return DomainKind.None;
	}
}

internal enum DomainKind
{
	None = 0,
	Boolean = 1,
	Guid = 2,
}
