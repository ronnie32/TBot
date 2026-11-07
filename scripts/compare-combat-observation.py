"""Compare a journal prediction with an explicitly attributed observed result.

Exit 0: within the sampled loss/round envelope; 2: outside; 1: invalid data or
different inputs. A match does not certify accuracy or authenticate the source.
The result envelope must include the complete unchanged SimulationInput.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys


def pairs(items):
    result={}
    for key,value in items:
        if key in result: raise ValueError('Duplicate JSON key: '+key)
        result[key]=value
    return result


def decode(text):
    def invalid_constant(value): raise ValueError('Nonfinite JSON value: '+value)
    return json.loads(text,object_pairs_hook=pairs,parse_constant=invalid_constant)


def compare(journal,actual):
    if set(actual)!={'PredictionId','Source','SimulationInput','Observed'}:
        raise ValueError('Result requires exactly PredictionId, Source, SimulationInput and Observed')
    if not isinstance(actual['Source'],str) or not actual['Source'].strip():
        raise ValueError('Result provenance Source is required')
    matching=[entry for entry in journal if entry.get('Id')==actual['PredictionId']]
    if len(matching)!=1: raise ValueError('PredictionId must identify exactly one journal entry')
    prediction=matching[0]
    if prediction.get('SchemaVersion')!=1 or prediction.get('Decision')!='Confirmed' or not prediction.get('SimulationInput'):
        raise ValueError('A confirmed schema-v1 prediction is required')
    if prediction['SimulationInput']!=actual['SimulationInput']:
        raise ValueError('Combat inputs differ; the result cannot validate this prediction')
    observed=actual['Observed']
    if not isinstance(observed,dict) or set(observed)!={'Outcome','AttackerLoss','Rounds'}:
        raise ValueError('Observed requires exactly Outcome, AttackerLoss and Rounds')
    if observed['Outcome'] not in ('Attacker','Defender','Draw'):
        raise ValueError('Invalid outcome')
    if type(observed['AttackerLoss']) is not int or observed['AttackerLoss']<0:
        raise ValueError('AttackerLoss must be a nonnegative integer resource value')
    if type(observed['Rounds']) is not int or not 0<=observed['Rounds']<=6:
        raise ValueError('Rounds must be an integer between 0 and 6')
    simulation=prediction['Simulation']
    if simulation['Runs']<1 or simulation['AttackerWins']!=simulation['Runs']:
        raise ValueError('Journal does not contain an all-winning confirmation')
    deviations=[]
    if observed['Outcome']!='Attacker': deviations.append('Outcome')
    if observed['AttackerLoss']>simulation['MaxAttackerLoss']: deviations.append('AttackerLoss')
    if observed['Rounds']>simulation['MaxRounds']: deviations.append('Rounds')
    encoded=json.dumps(prediction['SimulationInput'],sort_keys=True,separators=(',',':')).encode('utf-8')
    return {'SchemaVersion':1,'PredictionId':prediction['Id'],'Source':actual['Source'],
            'InputSha256':hashlib.sha256(encoded).hexdigest(),
            'Status':'OutsideSampleEnvelope' if deviations else 'WithinSampleEnvelope',
            'Deviations':deviations,'Observed':observed,'Predicted':simulation,
            'CertifiesAccuracy':False,'SourceAuthenticated':False,
            'Limitations':'Same declared inputs only; sampled maxima are not confidence bounds. A deviation may arise from randomness, changed/hidden inputs or model error.'}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--journal',required=True,type=Path)
    parser.add_argument('--result',required=True,type=Path)
    parser.add_argument('--output',type=Path)
    args=parser.parse_args()
    try:
        journal=[decode(line) for line in args.journal.read_text(encoding='utf-8').splitlines() if line.strip()]
        actual=decode(args.result.read_text(encoding='utf-8'))
        comparison=compare(journal,actual)
        text=json.dumps(comparison,indent=2)+'\n'
        if args.output:
            if args.output.resolve() in (args.journal.resolve(),args.result.resolve()):
                raise ValueError('Output must not overwrite evidence inputs')
            args.output.write_text(text,encoding='utf-8')
        else: print(text,end='')
        return 2 if comparison['Deviations'] else 0
    except (ValueError,KeyError,TypeError,OSError) as error:
        print(str(error),file=sys.stderr)
        return 1


if __name__=='__main__': sys.exit(main())
