using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;

namespace MyEverything {
	class VolumeMonitor {

		public Action<MyEverythingRecord> RecordAddedEvent;
		public Action<MyEverythingRecord> RecordDeletedEvent;
		public Action<MyEverythingRecord, MyEverythingRecord> RecordRenameEvent;

		public void Monitor(List<string> volumes, MyEverythingDB db) {
			foreach (var volume in volumes) {
				if (string.IsNullOrEmpty(volume)) throw new InvalidOperationException("Volume cant't be null or empty string.");
				if (!db.ContainsVolume(volume)) throw new InvalidOperationException(string.Format("Volume {0} must be scaned first."));
				Thread th = new Thread(new ParameterizedThreadStart(MonitorThread));
				th.Start(new Dictionary<string, object> { { "Volume", volume }, { "MyEverythingDB", db } });
			}
		}

		private PInvokeWin32.READ_USN_JOURNAL_DATA SetupInputData4JournalRead(string volume, uint reason) {
			IntPtr pMonitorVolume = MyEverything.GetVolumeJournalHandle(volume);
			uint bytesReturned = 0;
			PInvokeWin32.USN_JOURNAL_DATA ujd = new PInvokeWin32.USN_JOURNAL_DATA();
			MyEverything.QueryUSNJournal(pMonitorVolume, out ujd, out bytesReturned);

			// 构建输入参数
			PInvokeWin32.READ_USN_JOURNAL_DATA rujd = new PInvokeWin32.READ_USN_JOURNAL_DATA();
			rujd.StartUsn          = ujd.NextUsn;
			rujd.ReasonMask        = reason;
			rujd.ReturnOnlyOnClose = 1;
			rujd.Timeout           = 0;
			rujd.BytesToWaitFor    = 1;
			rujd.UsnJournalID      = ujd.UsnJournalID;

			return rujd;
		}

		private void MonitorThread(object param) {

			MyEverythingDB db = (param as Dictionary<string, object>)["MyEverythingDB"] as MyEverythingDB;
			string volume = (param as Dictionary<string, object>)["Volume"] as string;
			IntPtr pbuffer = Marshal.AllocHGlobal(0x1000); // 构建输出参数
			PInvokeWin32.READ_USN_JOURNAL_DATA rujd = SetupInputData4JournalRead(volume, 0xFFFFFFFF); // 对所有类型的reason都监听
			UInt32 cbRead; // 用来存储实际输出的字节数
			IntPtr prujd; // 指向输入参数结构体的指针

			while (true) {

				// 构建输入参数的指针
				prujd = Marshal.AllocHGlobal(Marshal.SizeOf(rujd));
				PInvokeWin32.ZeroMemory(prujd, Marshal.SizeOf(rujd));
				Marshal.StructureToPtr(rujd, prujd, true);

				Debug.WriteLine("\nMoniting on {0}......", volume as object);
				IntPtr pVolume = MyEverything.GetVolumeJournalHandle(volume);

				bool fok = PInvokeWin32.DeviceIoControl(pVolume,
					PInvokeWin32.FSCTL_READ_USN_JOURNAL,
					prujd, Marshal.SizeOf(typeof(PInvokeWin32.READ_USN_JOURNAL_DATA)),
					pbuffer, 0x1000, out cbRead, IntPtr.Zero);

				IntPtr pRealData = pbuffer + Marshal.SizeOf(typeof(Int64)); // 返回的内存块头上的8个字节是一个usn_id, 从第9个字节开始才是record.
				uint offset = 0;

				if (fok) {
					while (offset + Marshal.SizeOf(typeof(Int64)) < cbRead) { // record可能有多个!
						PInvokeWin32.USN_RECORD usn = new PInvokeWin32.USN_RECORD(pRealData + (int) offset);
						ProcessUSN(usn, volume, db);
						offset += usn.RecordLength;
					}
				}

				Marshal.FreeHGlobal(prujd);
				rujd.StartUsn = Marshal.ReadInt64(pbuffer); // 返回的内存块头上的8个字节就是用来在进行下一次查询的
			}
		}
		private void ProcessUSN(PInvokeWin32.USN_RECORD usn, string volume, MyEverythingDB db) {
			var dbCached = db.FindByFrn(volume, usn.FRN);
			Debug.WriteLine("------USN[frn={0}]------", usn.FRN);
			Debug.WriteLine("FileName={0}, Reason={1}", usn.FileName, Reason.ReasonPrettyFormat(usn.Reason));
			Debug.WriteLine("FileName[Cached]={0}", dbCached == null ? "NoCache" as object : dbCached.FullPath as object);
			Debug.WriteLine("--------------------------------------");

			if (Util.MaskEqual(usn.Reason, Reason.USN_REASONS["USN_REASON_RENAME_NEW_NAME"]))
				ProcessRenameNewName(usn, volume, db);
			if ((usn.Reason & Reason.USN_REASONS["USN_REASON_FILE_CREATE"]) != 0)
				ProcessFileCreate(usn, volume, db);
			if (Util.MaskEqual(usn.Reason, Reason.USN_REASONS["USN_REASON_FILE_DELETE"]))
				ProcessFileDelete(usn, volume, db);
		}
		private void ProcessFileDelete(PInvokeWin32.USN_RECORD usn, string volume, MyEverythingDB db) {
			var cached = db.FindByFrn(volume, usn.FRN);
			if (cached == null) {
				return;
			} else {
				var isdelete = db.DeleteRecord(volume, usn.FRN);
				Debug.WriteLine(">>>> File {0} deleted {1}.", cached.FullPath, isdelete ? "successful" : "fail");
				if (RecordDeletedEvent != null)
					RecordDeletedEvent(cached);
			}
		}
		private void ProcessRenameNewName(PInvokeWin32.USN_RECORD usn, string volume, MyEverythingDB db) { 
			// frn 没有改变
			// newname = usn.FileName
			// 根据usn.FRN可以从db中获取oldname
			// db.update...
			MyEverythingRecord newRecord = MyEverythingRecord.ParseUSN(usn);
			string fullpath = newRecord.Name;
			db.FindRecordPath(newRecord, ref fullpath, db.GetFolderSource(volume));
			newRecord.FullPath = fullpath;
			var oldRecord = db.FindByFrn(volume, usn.FRN);
			string newname = newRecord.FullPath;
			Debug.WriteLine(">>>> RenameFile {0} to {1}", oldRecord.FullPath, newname);
			db.UpdateRecord(volume, newRecord, 
				usn.IsFolder ? MyEverythingRecordType.Folder : MyEverythingRecordType.File);
			if (RecordRenameEvent != null) RecordRenameEvent(oldRecord, newRecord);
			if (newname.Contains("$RECYCLE.BIN")) {
				Debug.WriteLine(">>>> Means {0} moved to recycle.", oldRecord.FullPath as object);
			}
		}
		private void ProcessFileCreate(PInvokeWin32.USN_RECORD usn, string volume, MyEverythingDB db) {
			MyEverythingRecord record = MyEverythingRecord.ParseUSN(usn);
			string fullpath = record.Name;
			db.FindRecordPath(record, ref fullpath, db.GetFolderSource(volume));
			record.FullPath = fullpath;
			db.AddRecord(volume, record, usn.IsFolder ? MyEverythingRecordType.Folder : MyEverythingRecordType.File);
			Debug.WriteLine(">>>> NewFile: {0}", record.FullPath as object);
			if (RecordAddedEvent != null)
				RecordAddedEvent(record);
		}
	}
}
