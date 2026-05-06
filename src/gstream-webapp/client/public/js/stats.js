/**
 * create display string array from RTCStatsReport
 * @param {RTCStatsReport} report - current RTCStatsReport
 * @param {RTCStatsReport} lastReport - latest RTCStatsReport
 * @return {Array<string>} - display string Array
 */
import { t } from "./i18n.js";

export function createDisplayStringArray(report, lastReport) {
  let array = new Array();

  report.forEach(stat => {
    if (stat.type === 'inbound-rtp') {
      array.push(`${stat.kind} ${t('stats.receiving')}`);

      if (stat.codecId != undefined) {
        const codec = report.get(stat.codecId);
        array.push(`${t('stats.codec')} ${codec.mimeType}`);

        if (codec.sdpFmtpLine) {
          codec.sdpFmtpLine.split(";").forEach(fmtp => {
            array.push(` - ${fmtp}`);
          });
        }

        if (codec.payloadType) {
          array.push(` - payloadType=${codec.payloadType}`);
        }

        if (codec.clockRate) {
          array.push(` - clockRate=${codec.clockRate}`);
        }

        if (codec.channels) {
          array.push(` - channels=${codec.channels}`);
        }
      }

      if (stat.kind == "video") {
        array.push(`${t('stats.decoder')} ${stat.decoderImplementation}`);
        array.push(`${t('stats.resolution')} ${stat.frameWidth}x${stat.frameHeight}`);
        array.push(`${t('stats.framerate')} ${stat.framesPerSecond}`);
      }

      if (lastReport && lastReport.has(stat.id)) {
        const lastStats = lastReport.get(stat.id);
        const duration = (stat.timestamp - lastStats.timestamp) / 1000;
        const bitrate = (8 * (stat.bytesReceived - lastStats.bytesReceived) / duration) / 1000;
        array.push(`${t('stats.bitrate')} ${bitrate.toFixed(2)} kbit/sec`);
      }
    } else if (stat.type === 'outbound-rtp') {
      array.push(`${stat.kind} ${t('stats.sending')}`);

      if (stat.codecId != undefined) {
        const codec = report.get(stat.codecId);
        array.push(`${t('stats.codec')} ${codec.mimeType}`);

        if (codec.sdpFmtpLine) {
          codec.sdpFmtpLine.split(";").forEach(fmtp => {
            array.push(` - ${fmtp}`);
          });
        }

        if (codec.payloadType) {
          array.push(` - payloadType=${codec.payloadType}`);
        }

        if (codec.clockRate) {
          array.push(` - clockRate=${codec.clockRate}`);
        }

        if (codec.channels) {
          array.push(` - channels=${codec.channels}`);
        }
      }

      if (stat.kind == "video") {
        array.push(`${t('stats.encoder')} ${stat.encoderImplementation}`);
        array.push(`${t('stats.resolution')} ${stat.frameWidth}x${stat.frameHeight}`);
        array.push(`${t('stats.framerate')} ${stat.framesPerSecond}`);
      }

      if (lastReport && lastReport.has(stat.id)) {
        const lastStats = lastReport.get(stat.id);
        const duration = (stat.timestamp - lastStats.timestamp) / 1000;
        const bitrate = (8 * (stat.bytesSent - lastStats.bytesSent) / duration) / 1000;
        array.push(`${t('stats.bitrate')} ${bitrate.toFixed(2)} kbit/sec`);
      }
    }
  });

  return array;
}
