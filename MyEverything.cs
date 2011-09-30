using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.IO;
using System.ComponentModel;

namespace MyEverything {
	public class MyEverything {

		static void Main(string[] args) {

			Console.Write("Input Volumes for Scan (eg: C: D: E:): ");
			List<string> volumes = Console.ReadLine().ToUpper().Split(new char[] {' '}).ToList();

			// 因为不同分区可能存在相同的 frn, 所以一定要把不同分区的数据分开保存
			// 每个分区是一个 List<MyEverythingRecord>
			Dictionary<string, List<MyEverythingRecord>> allVolumeFiles = new Dictionary<string, List<MyEverythingRecord>>();
			Dictionary<string, List<MyEverythingRecord>> allVolumeDirs = new Dictionary<string, List<MyEverythingRecord>>();
			MyEverythingDB db = new MyEverythingDB();


			// ----------------遍历 mft，找到指定 volume 上的所有文件和文件夹----------------
			Console.WriteLine("Note: If this is your first time run, it will take some time to open the NTFS Journal System.");
			var enumFilesTimeStart = DateTime.Now;
			foreach (string volume in volumes) {
				List<MyEverythingRecord> files;
				List<MyEverythingRecord> folders;
				Console.WriteLine("Scanning {0}...", volume);
				EnumerateVolume(volume, out files, out folders);
				
				db.AddRecord(volume, files, MyEverythingRecordType.File);
				db.AddRecord(volume, folders, MyEverythingRecordType.Folder);
				
				Console.WriteLine("Indexing {0}...", volume);
				db.BuildPath();
			}
			Console.WriteLine("{0}s file and {1} folder indexed, {2}ms has spent.", 
				db.FileCount, db.FolderCount, DateTime.Now.Subtract(enumFilesTimeStart).TotalMilliseconds);
			// -----------------------------------------------------------------------------



			// ---------------------------命令模式------------------------------
			Console.WriteLine("\nMyEverything version 0.0.1");
			Console.WriteLine("Type ? for help.");
			while (true) {
				Console.Write("MyEverything> ");
				string[] cmd = Console.ReadLine().Split(' ');
				long fileFoundCnt;
				long folderFoundCnt;
				List<MyEverythingRecord> found;

				switch (cmd[0].ToLower()) { 
					case "s":
						if (!string.IsNullOrEmpty(cmd[1])) {
							var searchtimestart = DateTime.Now;
							found = db.FindByName(cmd[1], out fileFoundCnt, out folderFoundCnt);
							var searchtimeend = DateTime.Now;
							if (cmd.Length == 3 && !string.IsNullOrEmpty(cmd[2])) {
								using (StreamWriter sw = new StreamWriter(cmd[2], false)) {
									found.ForEach(x => sw.WriteLine(string.IsNullOrEmpty(x.FullPath) ? x.Name : x.FullPath));
								}
								Console.WriteLine("{0} has written.", cmd[2]);
							} else {
								found.ForEach(x => Console.WriteLine(string.IsNullOrEmpty(x.FullPath) ? x.Name : x.FullPath));
							}
							Console.WriteLine("{0}s file and {1}s folder matched, {2}ms has spent.",
								fileFoundCnt, folderFoundCnt, searchtimeend.Subtract(searchtimestart).TotalMilliseconds);
						}
						break;
					case "?":
						Console.WriteLine("s filename [logfile]\tSearch files and folders, \n\t\t\tif logfile specified, the result \n\t\t\twill be written to the logfile, \n\t\t\teg: s .txt");
						Console.WriteLine("mo volume [debug]\tMonitor the volume, eg: mo E: debug\n\t\t\tIf debug param is specified, some debug msg\n\t\t\twill be print to screen.\n\t\t\tNote: multiple volume monitor is not stable now.");
						Console.WriteLine("x\t\t\tExit this app.");
						break;
					case "x":
						Environment.Exit(0);
						break;
					case "mo":
						bool pridebug = (cmd.Length == 3) && (cmd[2] == "debug");
						VolumeMonitor monitor = new VolumeMonitor();
						monitor.RecordAddedEvent += delegate(MyEverythingRecord record) {
							if (pridebug) Console.WriteLine(">>>> New file {0} added.", record.FullPath);
						};
						monitor.RecordDeletedEvent += delegate(MyEverythingRecord record) {
							if (pridebug) Console.WriteLine(">>>> File {0} deleted.", record.FullPath);
						};
						monitor.RecordRenameEvent += delegate(MyEverythingRecord oldRecord, MyEverythingRecord newRecord) {
							if (pridebug) Console.WriteLine(">>>> File {0} renamed to {1}.", oldRecord.FullPath, newRecord.FullPath);
						};
						monitor.Monitor(cmd[1].ToUpper().Split(' ').ToList(), db);
						Console.WriteLine("Moniting on {0}......", cmd[1].ToUpper());
						break;
				}
			}
		}

		private static void FindPath(MyEverythingRecord currRecord, ref string fullpath, Dictionary<ulong, MyEverythingRecord> dirSource) {
			if (currRecord.IsVolumeRoot) return;
			MyEverythingRecord nextRecord = null;
			if (!dirSource.TryGetValue(currRecord.ParentFrn, out nextRecord)) return;
			fullpath = string.Format("{0}{1}{2}", nextRecord.Name, Path.DirectorySeparatorChar, fullpath);
			FindPath(nextRecord, ref fullpath, dirSource);
		}
		public static void ConvertFrn2FullPath(List<MyEverythingRecord> fileRecords, Dictionary<ulong, MyEverythingRecord> dirSource) {
			foreach (MyEverythingRecord file in fileRecords) {
				string fullpath = file.Name;
				MyEverythingRecord fstDir = null;
				if (dirSource.TryGetValue(file.ParentFrn, out fstDir)) {
					if (fstDir.IsVolumeRoot) { // 针对根目录下的文件
						file.FullPath = string.Format("{0}{1}{2}", fstDir.Name, Path.DirectorySeparatorChar, fullpath);
					} else {
						FindPath(fstDir, ref fullpath, dirSource);
						file.FullPath = fullpath;
					}
				} else {
					file.FullPath = fullpath;
					//Console.WriteLine("Can't find {0}'s parent folder.", file.Name);
				}
			}
			var t1 = fileRecords.Where(x => x.Name == "guestbook.js").FirstOrDefault();
		}
		private static void AddVolumeRootRecord(string volumeName, ref List<MyEverythingRecord> dirs) {
		    string rightVolumeName = string.Concat("\\\\.\\", volumeName);
		    rightVolumeName = string.Concat(rightVolumeName, Path.DirectorySeparatorChar);
		    IntPtr hRoot = PInvokeWin32.CreateFile(rightVolumeName,
		        0,
		        PInvokeWin32.FILE_SHARE_READ | PInvokeWin32.FILE_SHARE_WRITE,
		        IntPtr.Zero,
		        PInvokeWin32.OPEN_EXISTING,
		        PInvokeWin32.FILE_FLAG_BACKUP_SEMANTICS,
		        IntPtr.Zero);

		    if (hRoot.ToInt32() != PInvokeWin32.INVALID_HANDLE_VALUE) {
		        PInvokeWin32.BY_HANDLE_FILE_INFORMATION fi = new PInvokeWin32.BY_HANDLE_FILE_INFORMATION();
		        bool bRtn = PInvokeWin32.GetFileInformationByHandle(hRoot, out fi);
		        if (bRtn) {
		            UInt64 fileIndexHigh = (UInt64)fi.FileIndexHigh;
		            UInt64 indexRoot = (fileIndexHigh << 32) | fi.FileIndexLow;

					dirs.Add(new MyEverythingRecord { FRN = indexRoot, Name = volumeName, ParentFrn = 0, IsVolumeRoot = true });
		        } else {
		            throw new IOException("GetFileInformationbyHandle() returned invalid handle",
		                new Win32Exception(Marshal.GetLastWin32Error()));
		        }
		        PInvokeWin32.CloseHandle(hRoot);
		    } else {
		        throw new IOException("Unable to get root frn entry", new Win32Exception(Marshal.GetLastWin32Error()));
		    }
		}
		public static void EnumerateVolume(string volumeName, out List<MyEverythingRecord> files, out List<MyEverythingRecord> dirs) {
			files = new List<MyEverythingRecord>();
			dirs = new List<MyEverythingRecord>();
			IntPtr medBuffer = IntPtr.Zero;
			IntPtr pVolume   = IntPtr.Zero;
			try {
				AddVolumeRootRecord(volumeName, ref dirs); // 获得指定的 volume 对应的 frn, 为将来确定 full path 时提供支持.
				pVolume = GetVolumeJournalHandle(volumeName); // 获得通过 CreateFile 函数返回的 handle 值.
				EnableVomuleJournal(pVolume); // 打开 Journal, 因为 Journal 默认是关闭的.

				SetupMFTEnumInBuffer(ref medBuffer, pVolume); // 为 EnumerateFiles 准备好参数, 即 medBuffer
				EnumerateFiles(pVolume, medBuffer, ref files, ref dirs);
			} catch (Exception e) {
				Console.WriteLine(e.Message, e);
				Exception innerException = e.InnerException;
				while (innerException != null) {
					Console.WriteLine(innerException.Message, innerException);
					innerException = innerException.InnerException;
				}
				throw new ApplicationException("Error in EnumerateVolume()", e);
			} finally {
				if (pVolume.ToInt32() != PInvokeWin32.INVALID_HANDLE_VALUE) {
					PInvokeWin32.CloseHandle(pVolume);
					if (medBuffer != IntPtr.Zero) {
						Marshal.FreeHGlobal(medBuffer);
					}
				}
			}
		}
		public static IntPtr GetVolumeJournalHandle(string volumeName) {
			string vol = string.Concat("\\\\.\\", volumeName);
			IntPtr pVolume = PInvokeWin32.CreateFile(vol,
					PInvokeWin32.GENERIC_READ | PInvokeWin32.GENERIC_WRITE,
					PInvokeWin32.FILE_SHARE_READ | PInvokeWin32.FILE_SHARE_WRITE,
					IntPtr.Zero,
					PInvokeWin32.OPEN_EXISTING,
					0,
					IntPtr.Zero);
			if (pVolume.ToInt32() == PInvokeWin32.INVALID_HANDLE_VALUE) {
				throw new IOException(string.Format("CreateFile(\"{0}\") returned invalid handle", volumeName),
					new Win32Exception(Marshal.GetLastWin32Error()));
			} else {
				return pVolume;
			}
		}
		unsafe public static void EnableVomuleJournal(IntPtr pVolume) {
			UInt64 MaximumSize = 0x800000;
			UInt64 AllocationDelta = 0x100000;
			UInt32 cb;
			PInvokeWin32.CREATE_USN_JOURNAL_DATA cujd;
			cujd.MaximumSize = MaximumSize;
			cujd.AllocationDelta = AllocationDelta;

			int sizeCujd = Marshal.SizeOf(cujd);
			IntPtr cujdBuffer = Marshal.AllocHGlobal(sizeCujd);
			PInvokeWin32.ZeroMemory(cujdBuffer, sizeCujd);
			Marshal.StructureToPtr(cujd, cujdBuffer, true);

			bool fOk = PInvokeWin32.DeviceIoControl(pVolume, PInvokeWin32.FSCTL_CREATE_USN_JOURNAL,
				cujdBuffer, sizeCujd, IntPtr.Zero, 0, out cb, IntPtr.Zero);
			if (!fOk) {
				throw new IOException("DeviceIoControl() returned false", new Win32Exception(Marshal.GetLastWin32Error()));
			}
		}
		unsafe public static bool QueryUSNJournal(IntPtr pVolume, out PInvokeWin32.USN_JOURNAL_DATA ujd, out uint bytesReturned) {
			bool bOK = PInvokeWin32.DeviceIoControl(
				pVolume, PInvokeWin32.FSCTL_QUERY_USN_JOURNAL,
				IntPtr.Zero,
				0,
				out ujd,
				sizeof(PInvokeWin32.USN_JOURNAL_DATA),
				out bytesReturned,
				IntPtr.Zero	
			);
			return bOK;
		}
		unsafe private static void SetupMFTEnumInBuffer(ref IntPtr medBuffer, IntPtr pVolume) {
			uint bytesReturned = 0;
			PInvokeWin32.USN_JOURNAL_DATA ujd = new PInvokeWin32.USN_JOURNAL_DATA();

			bool bOk = QueryUSNJournal(pVolume, out ujd, out bytesReturned);
			if (bOk) {
				PInvokeWin32.MFT_ENUM_DATA med;
				med.StartFileReferenceNumber = 0;
				med.LowUsn = 0;
				med.HighUsn = ujd.NextUsn;
				int sizeMftEnumData = Marshal.SizeOf(med);
				medBuffer = Marshal.AllocHGlobal(sizeMftEnumData);
				PInvokeWin32.ZeroMemory(medBuffer, sizeMftEnumData);
				Marshal.StructureToPtr(med, medBuffer, true);
			} else {
				throw new IOException("DeviceIoControl() returned false", new Win32Exception(Marshal.GetLastWin32Error()));
			}
		}
		unsafe private static void EnumerateFiles(IntPtr pVolume, IntPtr medBuffer, ref List<MyEverythingRecord> files, ref List<MyEverythingRecord> folders) {
			IntPtr pData = Marshal.AllocHGlobal(sizeof(UInt64) + 0x10000);
			PInvokeWin32.ZeroMemory(pData, sizeof(UInt64) + 0x10000);
			uint outBytesReturned = 0;

			while (false != PInvokeWin32.DeviceIoControl(pVolume, PInvokeWin32.FSCTL_ENUM_USN_DATA, medBuffer,
									sizeof(PInvokeWin32.MFT_ENUM_DATA), pData, sizeof(UInt64) + 0x10000, out outBytesReturned,
									IntPtr.Zero)) {
				IntPtr pUsnRecord = new IntPtr(pData.ToInt32() + sizeof(Int64));
				while (outBytesReturned > 60) {
					PInvokeWin32.USN_RECORD usn = new PInvokeWin32.USN_RECORD(pUsnRecord);

					if (usn.IsFolder) {
						folders.Add(new MyEverythingRecord {
							Name = usn.FileName,
							ParentFrn = usn.ParentFRN,
							FRN = usn.FRN
						});
					} else {
						files.Add(new MyEverythingRecord {
							Name = usn.FileName,
							ParentFrn = usn.ParentFRN,
							FRN = usn.FRN
						});
					}

					pUsnRecord = new IntPtr(pUsnRecord.ToInt32() + usn.RecordLength);
					outBytesReturned -= usn.RecordLength;
				}
				Marshal.WriteInt64(medBuffer, Marshal.ReadInt64(pData, 0));
			}
			Marshal.FreeHGlobal(pData);
		}
	}
}
