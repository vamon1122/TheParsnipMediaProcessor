ffmpeg.exe -i %1 -c:v libx264 -pix_fmt yuv420p -c:a aac -crf %6 -vf "scale=%4:%5,fps=fps=%7" -movflags faststart %2%3
