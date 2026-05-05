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

using System.Collections.Generic;
using System.Text;
using FirebirdSql.Data.FirebirdClient;

namespace FirebirdSql.Data.DomainTypeMapping;

// Per-FbDomainConnection cache mapping (relation, field) -> RDB$FIELD_SOURCE.
// Lookups are batched and cached forever for the lifetime of the connection;
// schema changes during a connection's lifetime are not picked up (typical
// ADO.NET assumption).
internal sealed class DomainSchemaCache
{
	private readonly Dictionary<(string Relation, string Field), string> _cache = new();
	private bool _isResolving;

	public string GetDomain(FbConnection connection, string relation, string field)
	{
		if (string.IsNullOrEmpty(relation) || string.IsNullOrEmpty(field))
			return null;

		var key = (relation.Trim(), field.Trim());
		if (key.Item1.Length == 0 || key.Item2.Length == 0)
			return null;

		if (_cache.TryGetValue(key, out var cached))
			return cached;

		// Single-row lookup if we missed the cache. This is best-effort: a
		// failure here must not break the user's normal queries.
		Fetch(connection, new[] { key });
		return _cache.TryGetValue(key, out cached) ? cached : null;
	}

	public void PrefetchMany(FbConnection connection, IEnumerable<(string Relation, string Field)> keys)
	{
		var needed = new List<(string Relation, string Field)>();
		foreach (var k in keys)
		{
			if (string.IsNullOrEmpty(k.Relation) || string.IsNullOrEmpty(k.Field))
				continue;
			var trimmed = (k.Relation.Trim(), k.Field.Trim());
			if (trimmed.Item1.Length == 0 || trimmed.Item2.Length == 0)
				continue;
			if (!_cache.ContainsKey(trimmed))
				needed.Add(trimmed);
		}
		if (needed.Count == 0)
			return;
		Fetch(connection, needed);
	}

	private void Fetch(FbConnection connection, IReadOnlyList<(string Relation, string Field)> needed)
	{
		if (_isResolving)
			return;

		// Negative cache up front: even if the fetch fails, we won't keep retrying.
		foreach (var k in needed)
			_cache[k] = null;

		_isResolving = true;
		try
		{
			var sql = BuildQuery(needed.Count);
			using (var cmd = new FbCommand(sql, connection))
			{
				for (var i = 0; i < needed.Count; i++)
				{
					cmd.Parameters.Add("@r" + i, needed[i].Relation);
					cmd.Parameters.Add("@f" + i, needed[i].Field);
				}
				using (var reader = cmd.ExecuteReader())
				{
					while (reader.Read())
					{
						var rel = reader.IsDBNull(0) ? null : reader.GetString(0)?.Trim();
						var fld = reader.IsDBNull(1) ? null : reader.GetString(1)?.Trim();
						var dom = reader.IsDBNull(2) ? null : reader.GetString(2)?.Trim();
						if (!string.IsNullOrEmpty(rel) && !string.IsNullOrEmpty(fld))
							_cache[(rel, fld)] = dom;
					}
				}
			}
		}
		catch
		{
			// Best-effort. Leave the negative cache in place.
		}
		finally
		{
			_isResolving = false;
		}
	}

	private static string BuildQuery(int count)
	{
		var sb = new StringBuilder();
		sb.Append("SELECT TRIM(rfr.RDB$RELATION_NAME), TRIM(rfr.RDB$FIELD_NAME), TRIM(rfr.RDB$FIELD_SOURCE) ");
		sb.Append("FROM RDB$RELATION_FIELDS rfr WHERE ");
		for (var i = 0; i < count; i++)
		{
			if (i > 0)
				sb.Append(" OR ");
			sb.Append("(rfr.RDB$RELATION_NAME = @r");
			sb.Append(i);
			sb.Append(" AND rfr.RDB$FIELD_NAME = @f");
			sb.Append(i);
			sb.Append(')');
		}
		return sb.ToString();
	}

	public void Clear()
	{
		_cache.Clear();
	}
}
