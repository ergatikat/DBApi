// <copyright file="DropDatabaseTest.cs">
// Copyright 2020 Alexandros Koutroulis <icyd3mon@gmail.com>
// This file is part of OmegaTests
// OmegaTests is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
// OmegaTests is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
// You should have received a copy of the GNU General Public License along with OmegaTests. If not, see http://www.gnu.org/licenses/.
// </copyright>
// <author>Alexandros Koutroulis</author>
// <date>2021-01-18</date>

using System;
using DBApi.QueryBuilder;
using Xunit;

namespace OmegaTests.QueryBuilder.Expressions
{
    public class DropDatabaseTest
    {
        [Fact]
        public void TestDropDatabaseExpression()
        {
            var expression = new DropDatabase("TestDatabase");
            Assert.Equal("DROP DATABASE TestDatabase", expression.ToString());
        }

        [Fact]
        public void TestDropDatabaseWithOutDbName()
        {
            Assert.Throws<ArgumentNullException>(() =>
            {
                var expression = new DropDatabase(string.Empty);
            });
        }
    }
}