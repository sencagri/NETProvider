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
using System.Collections.Generic;
using System.Reflection;
using FirebirdSql.Data.FirebirdClient;

namespace FirebirdSql.Data.DomainTypeMapping.Internal;

// Reads the wire-level SQL type of each prepared parameter on an FbCommand.
//
// This goes through reflection because FirebirdSql.Data.FirebirdClient does
// not expose its prepared-statement descriptor publicly. We touch:
//
//   FbCommand._statement                   (private field, StatementBase)
//     StatementBase.Parameters             (public abstract Descriptor)
//       Descriptor.Count                   (public short)
//       Descriptor[i]                      (public DbField indexer)
//         DbField.SqlType                  (public int)
//
// All accessor delegates are cached after the first call; runtime cost is one
// extra interface call per parameter per execute.
//
// If a future provider release reorganises these names, the reflection probe
// fails closed: GetParameterSqlTypes returns null and the caller falls back to
// "no conversion" (the user gets the same exception they would have without
// this package, which is the correct behaviour — better than silently sending
// wrong values).
internal static class StatementReflection
{
	private static FieldInfo s_statementField;
	private static PropertyInfo s_parametersProperty;
	private static PropertyInfo s_descriptorCount;
	private static PropertyInfo s_descriptorItem;
	private static PropertyInfo s_dbFieldSqlType;
	private static bool s_initialized;
	private static bool s_available;

	private static readonly object s_initLock = new();

	private static void EnsureInitialized()
	{
		if (s_initialized)
			return;
		lock (s_initLock)
		{
			if (s_initialized)
				return;
			try
			{
				var fbCommandType = typeof(FbCommand);
				s_statementField = fbCommandType.GetField("_statement", BindingFlags.Instance | BindingFlags.NonPublic);
				if (s_statementField == null)
					return;

				var statementBaseType = s_statementField.FieldType;
				s_parametersProperty = statementBaseType.GetProperty("Parameters", BindingFlags.Instance | BindingFlags.Public);
				if (s_parametersProperty == null)
					return;

				var descriptorType = s_parametersProperty.PropertyType;
				s_descriptorCount = descriptorType.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
				s_descriptorItem = descriptorType.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public);
				if (s_descriptorCount == null || s_descriptorItem == null)
					return;

				var dbFieldType = s_descriptorItem.PropertyType;
				s_dbFieldSqlType = dbFieldType.GetProperty("SqlType", BindingFlags.Instance | BindingFlags.Public);
				if (s_dbFieldSqlType == null)
					return;

				s_available = true;
			}
			catch
			{
				s_available = false;
			}
			finally
			{
				s_initialized = true;
			}
		}
	}

	// Returns the wire-level SqlType (Firebird IscCodes.SQL_*) for each parameter
	// in command order, or null if the provider's internals are not accessible.
	// command.Prepare() must have been called first.
	public static int[] GetParameterSqlTypes(FbCommand command)
	{
		if (command == null)
			return null;
		EnsureInitialized();
		if (!s_available)
			return null;

		try
		{
			var statement = s_statementField.GetValue(command);
			if (statement == null)
				return null;
			var parameters = s_parametersProperty.GetValue(statement);
			if (parameters == null)
				return null;
			var count = (short)s_descriptorCount.GetValue(parameters);
			if (count <= 0)
				return Array.Empty<int>();
			var result = new int[count];
			var indexBuffer = new object[1];
			for (var i = 0; i < count; i++)
			{
				indexBuffer[0] = i;
				var field = s_descriptorItem.GetValue(parameters, indexBuffer);
				result[i] = (int)s_dbFieldSqlType.GetValue(field);
			}
			return result;
		}
		catch
		{
			return null;
		}
	}
}

// Mirrors a small slice of FirebirdSql.Data.Common.IscCodes that we need for
// type classification. Kept private so we don't accidentally drift from the
// provider's values — these constants are part of Firebird's wire protocol
// and are stable across versions.
internal static class FirebirdSqlTypeCodes
{
	public const int SQL_TEXT = 452;
	public const int SQL_VARYING = 448;
	public const int SQL_SHORT = 500;
	public const int SQL_LONG = 496;
	public const int SQL_FLOAT = 482;
	public const int SQL_DOUBLE = 480;
	public const int SQL_D_FLOAT = 530;
	public const int SQL_TIMESTAMP = 510;
	public const int SQL_BLOB = 520;
	public const int SQL_ARRAY = 540;
	public const int SQL_QUAD = 550;
	public const int SQL_TYPE_TIME = 560;
	public const int SQL_TYPE_DATE = 570;
	public const int SQL_INT64 = 580;
	public const int SQL_TIMESTAMP_TZ_EX = 32748;
	public const int SQL_TIME_TZ_EX = 32750;
	public const int SQL_INT128 = 32752;
	public const int SQL_TIMESTAMP_TZ = 32754;
	public const int SQL_TIME_TZ = 32756;
	public const int SQL_DEC16 = 32760;
	public const int SQL_DEC34 = 32762;
	public const int SQL_BOOLEAN = 32764;
	public const int SQL_NULL = 32766;

	// Strip the nullable bit (LSB).
	public static int Normalize(int sqlType) => sqlType & ~1;

	public static bool IsIntegerLike(int sqlType)
	{
		switch (Normalize(sqlType))
		{
			case SQL_SHORT:
			case SQL_LONG:
			case SQL_INT64:
			case SQL_INT128:
				return true;
			default:
				return false;
		}
	}
}
