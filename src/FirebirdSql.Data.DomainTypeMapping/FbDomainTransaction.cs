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

using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;

namespace FirebirdSql.Data.DomainTypeMapping;

public sealed class FbDomainTransaction : DbTransaction
{
	private readonly FbDomainConnection _connection;
	private readonly FbTransaction _inner;

	internal FbDomainTransaction(FbDomainConnection connection, FbTransaction inner)
	{
		_connection = connection;
		_inner = inner;
	}

	public FbTransaction InnerTransaction => _inner;
	public override IsolationLevel IsolationLevel => _inner.IsolationLevel;
	protected override DbConnection DbConnection => _connection;

	public override void Commit() => _inner.Commit();
	public override void Rollback() => _inner.Rollback();

	public override Task CommitAsync(CancellationToken cancellationToken = default) => _inner.CommitAsync(cancellationToken);
	public override Task RollbackAsync(CancellationToken cancellationToken = default) => _inner.RollbackAsync(cancellationToken);

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_inner?.Dispose();
		}
		base.Dispose(disposing);
	}
}
