using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MyEverything {
	class Util {
		public static bool MaskEqual(uint target, uint compare) {
			return (target & compare) != 0;
		}
	}
}
