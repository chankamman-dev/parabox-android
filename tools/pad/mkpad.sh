#!/usr/bin/env bash
# usage: mkpad.sh "<steps>" <alive ms> > out.json   steps: D U L R A B S(start) wNNN(wait ms)
R="$(dirname "$0")/register.json"
head -1 "$R"
echo '{"id":1,"command":"delay","duration":3000}'
for s in $1; do
  case $s in
    D) e="3,17,1"; z="3,17,0";; U) e="3,17,-1"; z="3,17,0";; L) e="3,16,-1"; z="3,16,0";; R) e="3,16,1"; z="3,16,0";;
    A) e="1,304,1"; z="1,304,0";; B) e="1,305,1"; z="1,305,0";; S) e="1,315,1"; z="1,315,0";;
    w*) echo "{\"id\":1,\"command\":\"delay\",\"duration\":${s#w}}"; continue;;
  esac
  echo "{\"id\":1,\"command\":\"inject\",\"events\":[$e,0,0,0]}"
  echo '{"id":1,"command":"delay","duration":120}'
  echo "{\"id\":1,\"command\":\"inject\",\"events\":[$z,0,0,0]}"
  echo '{"id":1,"command":"delay","duration":350}'
done
echo "{\"id\":1,\"command\":\"delay\",\"duration\":$2}"
