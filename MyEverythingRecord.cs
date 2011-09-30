using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MyEverything {
	public class MyEverythingRecord {

		public string Name { get; set; }
		public ulong FRN { get; set; }
		public UInt64 ParentFrn { get; set; }
		public string FullPath { get; set; }
		public bool IsVolumeRoot { get; set; }

		public override string ToString() {
			return string.IsNullOrEmpty(FullPath) ? Name : FullPath;
		}

		public static MyEverythingRecord ParseUSN(PInvokeWin32.USN_RECORD usn) {
			return new MyEverythingRecord { FRN = usn.FRN, Name = usn.FileName, ParentFrn = usn.ParentFRN };
		}
	}
}
