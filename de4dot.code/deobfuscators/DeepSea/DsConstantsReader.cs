/*
    Copyright (C) 2011-2015 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.Collections.Generic;
using de4dot.blocks;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.DeepSea {
	class DsConstantsReader : ConstantsReader {
		public DsConstantsReader(List<Instr> instrs)
			: base(instrs) {
		}

		protected override bool GetLocalConstantInt32(Instruction instr, out int value) {
			value = 0;
			return true;
		}

		protected override bool GetArgConstantInt32(Instruction instr, out int value) {
			value = 0;
			return true;
		}
	}
}
