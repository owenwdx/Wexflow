﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Xml.Linq;
using Wexflow.Core;
using Wexflow.Core.Db;
using Workflow = Wexflow.Core.Workflow;

namespace Wexflow.Tasks.ApproveRecord
{
    public class ApproveRecord : Task
    {
        public string RecordId { get; private set; }
        public string AssignedTo { get; private set; }
        public string OnApproved { get; private set; }
        public string OnRejected { get; private set; }
        public string OnDeleted { get; private set; }
        public string OnStopped { get; private set; }

        public ApproveRecord(XElement xe, Workflow wf) : base(xe, wf)
        {
            RecordId = GetSetting("record");
            AssignedTo = GetSetting("assignedTo");
            OnApproved = GetSetting("onApproved");
            OnRejected = GetSetting("onRejected");
            OnDeleted = GetSetting("onDeleted");
            OnStopped = GetSetting("onStopped");
        }

        public override TaskStatus Run()
        {
            Info($"Approval process starting on the reocrd {RecordId} ...");

            var status = Core.Status.Success;

            try
            {
                if (Workflow.IsApproval)
                {
                    var trigger = Path.Combine(Workflow.ApprovalFolder, Workflow.Id.ToString(), Workflow.InstanceId.ToString(), Id.ToString(), "task.approved");

                    if (string.IsNullOrEmpty(RecordId))
                    {
                        Error("The record id setting is empty.");
                        status = Core.Status.Error;
                    }
                    else if (string.IsNullOrEmpty(AssignedTo))
                    {
                        Error("The assignedTo id setting is empty.");
                        status = Core.Status.Error;
                    }
                    else
                    {
                        var record = Workflow.Database.GetRecord(RecordId);
                        var recordName = string.Empty;

                        if (record == null)
                        {
                            Error($"Record {RecordId} does not exist in the database.");
                            status = Core.Status.Error;
                        }
                        else
                        {
                            recordName = record.Name;
                            var assignedTo = Workflow.Database.GetUser(AssignedTo);

                            if (assignedTo == null)
                            {
                                Error($"The user {AssignedTo} does not exist in the database.");
                                status = Core.Status.Error;
                            }
                            else
                            {
                                // notification onStart
                                var assignedBy = Workflow.Database.GetUser(Workflow.StartedBy);
                                var notificationMessage = $"An approval process on the record {record.Name} has started. You must update that record by adding new file versions. You can also add comments on that record.";
                                var notification = new Notification
                                {
                                    Message = notificationMessage,
                                    AssignedBy = assignedBy.GetDbId(),
                                    AssignedTo = assignedTo.GetDbId(),
                                    AssignedOn = DateTime.Now,
                                    IsRead = false
                                };
                                Workflow.Database.InsertNotification(notification);

                                if (Workflow.WexflowEngine.EnableEmailNotifications)
                                {
                                    string subject = "Wexflow notification from " + assignedBy.Username;
                                    string body = notificationMessage;

                                    string host = Workflow.WexflowEngine.SmptHost;
                                    int port = Workflow.WexflowEngine.SmtpPort;
                                    bool enableSsl = Workflow.WexflowEngine.SmtpEnableSsl;
                                    string smtpUser = Workflow.WexflowEngine.SmtpUser;
                                    string smtpPassword = Workflow.WexflowEngine.SmtpPassword;
                                    string from = Workflow.WexflowEngine.SmtpFrom;

                                    Send(host, port, enableSsl, smtpUser, smtpPassword, assignedTo.Email, from, subject, body);
                                }

                                Info($"ApproveRecord.OnStart: User {assignedTo.Username} notified for the start of approval process on the record {record.GetDbId()} - {record.Name}.");

                                // assign the record
                                record.ModifiedBy = assignedBy.GetDbId();
                                record.AssignedTo = assignedTo.GetDbId();
                                record.AssignedOn = DateTime.Now;
                                Workflow.Database.UpdateRecord(record.GetDbId(), record);
                                Info($"Record {record.GetDbId()} - {record.Name} assigned to {assignedTo.Username}.");

                                IsWaitingForApproval = true;
                                Workflow.IsWaitingForApproval = true;

                                while (true)
                                {
                                    // notification onRecordDeleted
                                    record = Workflow.Database.GetRecord(RecordId);
                                    if (record == null)
                                    {
                                        notificationMessage = $"The approval process on the record {recordName} was stopped because the record was deleted.";
                                        notification = new Notification
                                        {
                                            Message = notificationMessage,
                                            AssignedBy = assignedBy.GetDbId(),
                                            AssignedTo = assignedTo.GetDbId(),
                                            AssignedOn = DateTime.Now,
                                            IsRead = false
                                        };
                                        Workflow.Database.InsertNotification(notification);

                                        if (Workflow.WexflowEngine.EnableEmailNotifications)
                                        {
                                            string subject = "Wexflow notification from " + assignedBy.Username;
                                            string body = notificationMessage;

                                            string host = Workflow.WexflowEngine.SmptHost;
                                            int port = Workflow.WexflowEngine.SmtpPort;
                                            bool enableSsl = Workflow.WexflowEngine.SmtpEnableSsl;
                                            string smtpUser = Workflow.WexflowEngine.SmtpUser;
                                            string smtpPassword = Workflow.WexflowEngine.SmtpPassword;
                                            string from = Workflow.WexflowEngine.SmtpFrom;

                                            Send(host, port, enableSsl, smtpUser, smtpPassword, assignedTo.Email, from, subject, body);
                                        }

                                        Info($"ApproveRecord.OnRecordDeleted: User {assignedTo.Username} notified for the removal of the record {RecordId}.");

                                        var tasks = GetTasks(OnDeleted);
                                        ClearFiles();
                                        foreach (var task in tasks)
                                        {
                                            task.Run();
                                        }

                                        break;
                                    }

                                    // notification onApproved
                                    if (File.Exists(trigger))
                                    {
                                        notificationMessage = $"The record {record.Name} was approved by the user {Workflow.ApprovedBy}.";
                                        notification = new Notification
                                        {
                                            Message = notificationMessage,
                                            AssignedBy = assignedBy.GetDbId(),
                                            AssignedTo = assignedTo.GetDbId(),
                                            AssignedOn = DateTime.Now,
                                            IsRead = false
                                        };
                                        Workflow.Database.InsertNotification(notification);

                                        if (Workflow.WexflowEngine.EnableEmailNotifications)
                                        {
                                            string subject = "Wexflow notification from " + assignedBy.Username;
                                            string body = notificationMessage;

                                            string host = Workflow.WexflowEngine.SmptHost;
                                            int port = Workflow.WexflowEngine.SmtpPort;
                                            bool enableSsl = Workflow.WexflowEngine.SmtpEnableSsl;
                                            string smtpUser = Workflow.WexflowEngine.SmtpUser;
                                            string smtpPassword = Workflow.WexflowEngine.SmtpPassword;
                                            string from = Workflow.WexflowEngine.SmtpFrom;

                                            Send(host, port, enableSsl, smtpUser, smtpPassword, assignedTo.Email, from, subject, body);
                                        }

                                        Info($"ApproveRecord.OnApproved: User {assignedTo.Username} notified for the approval of the record {record.GetDbId()} - {record.Name}.");

                                        // update the record
                                        record.Approved = true;
                                        Workflow.Database.UpdateRecord(record.GetDbId(), record);
                                        Info($"Record {record.GetDbId()} - {record.Name} updated.");


                                        var tasks = GetTasks(OnApproved);
                                        var latestVersion = Workflow.Database.GetLatestVersion(RecordId);
                                        if (latestVersion != null)
                                        {
                                            ClearFiles();
                                            Files.Add(new FileInf(latestVersion.FilePath, Id));
                                        }

                                        foreach (var task in tasks)
                                        {
                                            task.Run();
                                        }

                                        if (latestVersion != null)
                                        {
                                            Files.RemoveAll(f => f.Path == latestVersion.FilePath);
                                        }

                                        break;
                                    }

                                    // notification onRejected
                                    if (Workflow.IsRejected)
                                    {
                                        notificationMessage = $"The record {record.Name} was rejected by the user {Workflow.RejectedBy}.";
                                        notification = new Notification
                                        {
                                            Message = notificationMessage,
                                            AssignedBy = assignedBy.GetDbId(),
                                            AssignedTo = assignedTo.GetDbId(),
                                            AssignedOn = DateTime.Now,
                                            IsRead = false
                                        };
                                        Workflow.Database.InsertNotification(notification);

                                        if (Workflow.WexflowEngine.EnableEmailNotifications)
                                        {
                                            string subject = "Wexflow notification from " + assignedBy.Username;
                                            string body = notificationMessage;

                                            string host = Workflow.WexflowEngine.SmptHost;
                                            int port = Workflow.WexflowEngine.SmtpPort;
                                            bool enableSsl = Workflow.WexflowEngine.SmtpEnableSsl;
                                            string smtpUser = Workflow.WexflowEngine.SmtpUser;
                                            string smtpPassword = Workflow.WexflowEngine.SmtpPassword;
                                            string from = Workflow.WexflowEngine.SmtpFrom;

                                            Send(host, port, enableSsl, smtpUser, smtpPassword, assignedTo.Email, from, subject, body);
                                        }

                                        Info($"ApproveRecord.OnRejected: User {assignedTo.Username} notified for the rejection of the record {record.GetDbId()} - {record.Name}.");

                                        // update the record
                                        record.Approved = false;
                                        Workflow.Database.UpdateRecord(record.GetDbId(), record);
                                        Info($"Record {record.GetDbId()} - {record.Name} updated.");

                                        var tasks = GetTasks(OnRejected);
                                        var latestVersion = Workflow.Database.GetLatestVersion(RecordId);
                                        if (latestVersion != null)
                                        {
                                            ClearFiles();
                                            Files.Add(new FileInf(latestVersion.FilePath, Id));
                                        }

                                        foreach (var task in tasks)
                                        {
                                            task.Run();
                                        }

                                        if (latestVersion != null)
                                        {
                                            Files.RemoveAll(f => f.Path == latestVersion.FilePath);
                                        }

                                        break;
                                    }

                                    // notification onStopped
                                    if (IsStopped)
                                    {
                                        notificationMessage = $"The approval process on the record {record.Name} was stopped by the user {Workflow.StoppedBy}.";
                                        notification = new Notification
                                        {
                                            Message = notificationMessage,
                                            AssignedBy = assignedBy.GetDbId(),
                                            AssignedTo = assignedTo.GetDbId(),
                                            AssignedOn = DateTime.Now,
                                            IsRead = false
                                        };
                                        Workflow.Database.InsertNotification(notification);
                                        Info($"ApproveRecord.OnStopped: User {assignedTo.Username} notified for the stop of the approval process of the record {record.GetDbId()} - {record.Name}.");

                                        var tasks = GetTasks(OnStopped);
                                        var latestVersion = Workflow.Database.GetLatestVersion(RecordId);
                                        if (latestVersion != null)
                                        {
                                            ClearFiles();
                                            Files.Add(new FileInf(latestVersion.FilePath, Id));
                                        }

                                        foreach (var task in tasks)
                                        {
                                            task.Run();
                                        }

                                        if (latestVersion != null)
                                        {
                                            Files.RemoveAll(f => f.Path == latestVersion.FilePath);
                                        }

                                        break;
                                    }

                                    Thread.Sleep(1000);
                                }

                                IsWaitingForApproval = false;
                                Workflow.IsWaitingForApproval = false;
                                if (!Workflow.IsRejected && !IsStopped)
                                {
                                    InfoFormat("Task approved: {0}", trigger);
                                }
                                else if (!IsStopped)
                                {
                                    Info("This workflow has been rejected.");
                                }

                                if (File.Exists(trigger))
                                {
                                    File.Delete(trigger);
                                }
                            }
                        }
                    }
                }
                else
                {
                    Error("This workflow is not an approval workflow. Mark this workflow as an approval workflow to use this task.");
                    status = Core.Status.Error;
                }
            }
            catch (ThreadAbortException)
            {
                var record = Workflow.Database.GetRecord(RecordId);
                if (record != null)
                {
                    var assignedBy = Workflow.Database.GetUser(Workflow.StartedBy);
                    var assignedTo = Workflow.Database.GetUser(AssignedTo);
                    if (assignedBy != null && assignedTo != null)
                    {
                        var notificationMessage = $"The approval process on the record {record.Name} was stopped by the user {Workflow.StoppedBy}.";
                        var notification = new Notification
                        {
                            Message = notificationMessage,
                            AssignedBy = assignedBy.GetDbId(),
                            AssignedTo = assignedTo.GetDbId(),
                            AssignedOn = DateTime.Now,
                            IsRead = false
                        };
                        Workflow.Database.InsertNotification(notification);

                        if (Workflow.WexflowEngine.EnableEmailNotifications)
                        {
                            string subject = "Wexflow notification from " + assignedBy.Username;
                            string body = notificationMessage;

                            string host = Workflow.WexflowEngine.SmptHost;
                            int port = Workflow.WexflowEngine.SmtpPort;
                            bool enableSsl = Workflow.WexflowEngine.SmtpEnableSsl;
                            string smtpUser = Workflow.WexflowEngine.SmtpUser;
                            string smtpPassword = Workflow.WexflowEngine.SmtpPassword;
                            string from = Workflow.WexflowEngine.SmtpFrom;

                            Send(host, port, enableSsl, smtpUser, smtpPassword, assignedTo.Email, from, subject, body);
                        }

                        Info($"ApproveRecord.OnStopped: User {assignedTo.Username} notified for the stop of the approval process of the record {record.GetDbId()} - {record.Name}.");

                        var tasks = GetTasks(OnStopped);
                        var latestVersion = Workflow.Database.GetLatestVersion(RecordId);
                        if (latestVersion != null)
                        {
                            ClearFiles();
                            Files.Add(new FileInf(latestVersion.FilePath, Id));
                        }

                        foreach (var task in tasks)
                        {
                            task.Run();
                        }

                        if (latestVersion != null)
                        {
                            Files.RemoveAll(f => f.Path == latestVersion.FilePath);
                        }
                    }
                }

                throw;
            }
            catch (Exception e)
            {
                Error("An error occured during approval process.", e);
                status = Core.Status.Error;
            }

            Info("Approval process finished.");
            return new TaskStatus(status);
        }

        private void ClearFiles()
        {
            foreach (var task in Workflow.Tasks)
            {
                task.Files.Clear();
            }
        }

        private Task[] GetTasks(string evt)
        {
            List<Task> tasks = new List<Task>();

            if (!string.IsNullOrEmpty(evt))
            {
                var ids = evt.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var id in ids)
                {
                    var taskId = int.Parse(id.Trim());
                    var task = Workflow.Tasks.First(t => t.Id == taskId);
                    tasks.Add(task);
                }
            }

            return tasks.ToArray();
        }

        private void Send(string host, int port, bool enableSsl, string user, string password, string to, string from, string subject, string body)
        {
            var smtp = new SmtpClient
            {
                Host = host,
                Port = port,
                EnableSsl = enableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(user, password)
            };

            using (var msg = new MailMessage())
            {
                msg.From = new MailAddress(from);
                msg.To.Add(new MailAddress(to));
                msg.Subject = subject;
                msg.Body = body;

                smtp.Send(msg);
            }
        }
    }
}
